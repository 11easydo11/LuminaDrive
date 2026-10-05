using System.Diagnostics;
using System.Security.Cryptography;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Localization;
using UsbRecoverySuite.Core.Services.Logging;

namespace UsbRecoverySuite.Core.Services.Capacity;

public class CapacityTestService : ICapacityTestService
{
    private readonly IAppLogger _logger;
    private readonly ILocalizationService? _localization;
    private const int ChunkSizeBytes = 4 * 1024 * 1024; // 4 MB chunks
    private static readonly byte[] MagicHeader = "USBRECOV"u8.ToArray();

    public CapacityTestService(IAppLogger logger, ILocalizationService? localization = null)
    {
        _logger = logger;
        _localization = localization;
    }

    public async Task<CapacityTestResult> RunCapacityTestAsync(
        UsbDeviceInfo device,
        long testSizeBytes,
        IProgress<CapacityProgressReport> progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var result = new CapacityTestResult
            {
                ReportedCapacityBytes = device.ReportedCapacityBytes,
                AddressableCapacityBytes = device.Partitions.Sum(p => p.SizeBytes)
            };

            // Find target volume with available drive letter
            var volume = device.Volumes.FirstOrDefault(v => !string.IsNullOrEmpty(v.DriveLetter) && char.IsLetter(v.DriveLetter[0]))
                         ?? device.Partitions.Select(p => p.Volume).FirstOrDefault(v => v != null && !string.IsNullOrEmpty(v.DriveLetter));

            if (volume == null || string.IsNullOrEmpty(volume.DriveLetter))
            {
                result.Summary = Text("CapacityNoDriveLetter");
                result.DetailedFindings.Add("Target disk has no mounted filesystem volumes to perform verified write/read testing.");
                return result;
            }

            string driveRoot = volume.DriveLetter.TrimEnd('\\') + "\\";
            if (!Directory.Exists(driveRoot))
            {
                result.Summary = Text("CapacityDriveUnavailable", driveRoot);
                return result;
            }

            var driveInfo = new DriveInfo(driveRoot);
            long availableFreeSpace = driveInfo.AvailableFreeSpace;

            // Target size to test: min of requested, available free space (minus 32MB safety margin)
            long targetBytes = testSizeBytes > 0 ? Math.Min(testSizeBytes, availableFreeSpace - 32 * 1024 * 1024L) : (availableFreeSpace - 32 * 1024 * 1024L);
            if (targetBytes < ChunkSizeBytes)
            {
                result.Summary = Text("CapacityFreeSpaceLow");
                return result;
            }

            string testDir = Path.Combine(driveRoot, $".usb_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(testDir);
            }
            catch (Exception ex)
            {
                result.Summary = Text("CapacityCreateFailed", driveRoot, ex.Message);
                return result;
            }

            int totalChunks = (int)(targetBytes / ChunkSizeBytes);
            long totalBytesToTest = (long)totalChunks * ChunkSizeBytes;
            result.TestedBytes = totalBytesToTest;

            var sw = Stopwatch.StartNew();
            _logger.LogInfo(nameof(CapacityTestService), 
                $"Starting Capacity Test on {driveRoot} ({totalChunks} chunks of 4 MB = {VolumeInfo.FormatBytes(totalBytesToTest)})...");

            var chunkFiles = new List<(int Index, string Path)>();
            byte[] writeBuffer = new byte[ChunkSizeBytes];
            byte[] readBuffer = new byte[ChunkSizeBytes];

            long totalBytesWritten = 0;
            long totalBytesVerified = 0;
            long totalBytesReliable = 0;
            int writeErrors = 0;
            int readErrors = 0;
            bool fakeDetected = false;
            string fakeReason = string.Empty;
            string fakeEvidenceLocalized = string.Empty;

            var writeStopwatch = new Stopwatch();
            var readStopwatch = new Stopwatch();

            try
            {
                // PHASE 1: Sequential Write & Interleaved Early-Block Loop Detection
                for (int i = 0; i < totalChunks; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.IsCancelled = true;
                        break;
                    }

                    // Prepare chunk buffer: [MagicHeader (8)][ChunkIndex (4)][Deterministic Hash payload]
                    PrepareChunkBuffer(writeBuffer, i);
                    string filePath = Path.Combine(testDir, $"chunk_{i:D5}.dat");

                    writeStopwatch.Start();
                    try
                    {
                        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                        {
                            fs.Write(writeBuffer, 0, ChunkSizeBytes);
                            fs.Flush(true); // Ensure written to physical NAND
                        }
                        chunkFiles.Add((i, filePath));
                        totalBytesWritten += ChunkSizeBytes;
                    }
                    catch (Exception ex)
                    {
                        writeErrors++;
                        _logger.LogWarning(nameof(CapacityTestService), $"Write error at chunk #{i} ({VolumeInfo.FormatBytes((long)i * ChunkSizeBytes)}): {ex.Message}");
                    }
                    finally
                    {
                        writeStopwatch.Stop();
                    }

                    // Interleaved loop detection: every 16 chunks, verify chunk 0 hasn't been overwritten by a looping controller
                    var firstChunk = chunkFiles.FirstOrDefault(chunk => chunk.Index == 0);
                    if (i > 0 && i % 16 == 0 && firstChunk.Path != null)
                    {
                        try
                        {
                            byte[] probeBuf = new byte[ChunkSizeBytes];
                            using (var fs = new FileStream(firstChunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                            {
                                PrepareChunkBuffer(writeBuffer, 0);
                                fs.ReadExactly(probeBuf, 0, ChunkSizeBytes);
                                if (!ValidateChunkBuffer(probeBuf, 0, writeBuffer))
                                {
                                    fakeDetected = true;
                                    fakeReason = $"Looping address pattern detected! Early chunk #0 was corrupted or overwritten after writing chunk #{i} ({VolumeInfo.FormatBytes((long)i * ChunkSizeBytes)}).";
                                    fakeEvidenceLocalized = Text("CapacityFakeEvidenceLoop", i, VolumeInfo.FormatBytes((long)i * ChunkSizeBytes));
                                    _logger.LogError(nameof(CapacityTestService), fakeReason);
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // If chunk 0 file can't even be opened, corruption occurred
                            fakeDetected = true;
                            fakeReason = "Early test file disappeared or became unreadable during sequential write.";
                            fakeEvidenceLocalized = Text("CapacityFakeEvidenceUnreadable");
                            break;
                        }
                    }

                    // Report Write Progress
                    double writePct = (double)(i + 1) / totalChunks * 50.0; // Phase 1 is 0-50%
                    double currentWriteSpeed = writeStopwatch.Elapsed.TotalSeconds > 0 
                        ? (totalBytesWritten / 1024.0 / 1024.0) / writeStopwatch.Elapsed.TotalSeconds 
                        : 0.0;

                    progress.Report(new CapacityProgressReport
                    {
                        Percentage = writePct,
                        CurrentPhase = Text("CapacityWritingProgress", i + 1, totalChunks, VolumeInfo.FormatBytes(totalBytesWritten)),
                        BytesProcessed = totalBytesWritten,
                        TotalBytesToProcess = totalBytesToTest * 2,
                        SpeedMBps = currentWriteSpeed,
                        ElapsedTime = sw.Elapsed,
                        RemainingTime = TimeSpan.FromSeconds(currentWriteSpeed > 0 ? (totalBytesToTest - totalBytesWritten) / 1024.0 / 1024.0 / currentWriteSpeed : 0),
                        ErrorsFound = writeErrors + readErrors
                    });
                }

                // PHASE 2: Full Readback & Verification
                if (!result.IsCancelled && !fakeDetected)
                {
                    for (int i = 0; i < chunkFiles.Count; i++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            result.IsCancelled = true;
                            break;
                        }

                        var chunk = chunkFiles[i];
                        string filePath = chunk.Path;
                        readStopwatch.Start();

                        try
                        {
                            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                            {
                                PrepareChunkBuffer(writeBuffer, chunk.Index);
                                fs.ReadExactly(readBuffer, 0, ChunkSizeBytes);
                                totalBytesVerified += ChunkSizeBytes;
                                if (ValidateChunkBuffer(readBuffer, chunk.Index, writeBuffer))
                                {
                                    totalBytesReliable += ChunkSizeBytes;
                                }
                                else
                                {
                                    readErrors++;
                            _logger.LogWarning(nameof(CapacityTestService), $"Read/content verification mismatch at chunk #{chunk.Index}!");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            readErrors++;
                        _logger.LogWarning(nameof(CapacityTestService), $"Read failure at chunk #{chunk.Index}: {ex.Message}");
                        }
                        finally
                        {
                            readStopwatch.Stop();
                        }

                        // Report Read Progress
                        double readPct = 50.0 + ((double)(i + 1) / chunkFiles.Count * 50.0); // Phase 2 is 50-100%
                        double currentReadSpeed = readStopwatch.Elapsed.TotalSeconds > 0 
                            ? (totalBytesVerified / 1024.0 / 1024.0) / readStopwatch.Elapsed.TotalSeconds 
                            : 0.0;

                        progress.Report(new CapacityProgressReport
                        {
                            Percentage = readPct,
                            CurrentPhase = Text("CapacityVerifyingProgress", i + 1, chunkFiles.Count, VolumeInfo.FormatBytes(totalBytesVerified)),
                            BytesProcessed = totalBytesToTest + totalBytesVerified,
                            TotalBytesToProcess = totalBytesToTest * 2,
                            SpeedMBps = currentReadSpeed,
                            ElapsedTime = sw.Elapsed,
                            RemainingTime = TimeSpan.FromSeconds(currentReadSpeed > 0 ? (totalBytesToTest - totalBytesVerified) / 1024.0 / 1024.0 / currentReadSpeed : 0),
                            ErrorsFound = writeErrors + readErrors
                        });
                    }
                }
            }
            finally
            {
                // Clean up all test files safely
                try
                {
                    if (Directory.Exists(testDir))
                    {
                        Directory.Delete(testDir, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(nameof(CapacityTestService), $"Failed to clean up test directory: {ex.Message}");
                }
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            result.VerifiedCapacityBytes = totalBytesVerified;
            result.ReliableCapacityBytes = totalBytesReliable;
            result.WriteErrorsCount = writeErrors;
            result.ReadErrorsCount = readErrors;
            result.AverageWriteSpeedMBps = writeStopwatch.Elapsed.TotalSeconds > 0 
                ? (totalBytesWritten / 1024.0 / 1024.0) / writeStopwatch.Elapsed.TotalSeconds 
                : 0.0;
            result.AverageReadSpeedMBps = readStopwatch.Elapsed.TotalSeconds > 0 
                ? (totalBytesVerified / 1024.0 / 1024.0) / readStopwatch.Elapsed.TotalSeconds 
                : 0.0;

            result.IsCompleted = !result.IsCancelled && !fakeDetected &&
                                 chunkFiles.Count == totalChunks && totalBytesVerified == totalBytesToTest;

            // Analyze fake drive indicators
            if (fakeDetected)
            {
                result.FakeDriveSuspected = true;
                result.FakeDriveConfidencePercent = 95;
                result.FakeDriveEvidence = fakeEvidenceLocalized;
                result.DetailedFindings.Add($"SUSPECTED FAKE CAPACITY: {fakeReason}");
                result.Summary = Text("CapacityFakeDetected");
            }
            else if (result.TotalErrorsCount > 0)
            {
                double errorRate = (double)result.TotalErrorsCount / Math.Max(1, totalChunks);
                if (errorRate > 0.3)
                {
                    result.FakeDriveSuspected = true;
                    result.FakeDriveConfidencePercent = 75;
                    result.FakeDriveEvidence = Text("CapacityFakeEvidenceErrorRate", result.TotalErrorsCount, totalChunks);
                    result.Summary = Text("CapacitySevereErrors");
                }
                else
                {
                    result.FakeDriveSuspected = false;
                    result.Summary = Text("CapacityErrors", result.TotalErrorsCount);
                }
                result.DetailedFindings.Add($"Write errors: {writeErrors}, Read/verification errors: {readErrors}.");
            }
            else if (result.IsCancelled)
            {
                result.Summary = Text("CapacityCancelled");
            }
            else
            {
                result.FakeDriveSuspected = false;
                result.Summary = Text("CapacitySuccess", VolumeInfo.FormatBytes(totalBytesReliable));
                result.DetailedFindings.Add($"Average Write Speed: {result.AverageWriteSpeedMBps:F2} MB/s");
                result.DetailedFindings.Add($"Average Read Speed: {result.AverageReadSpeedMBps:F2} MB/s");
                result.DetailedFindings.Add("No flash looping or memory corruption detected in tested space.");
            }

            _logger.LogInfo(nameof(CapacityTestService), $"Capacity test finished: {result.Summary}");
            return result;
        });
    }

    private string Text(string key, params object[] arguments) =>
        _localization?.Get(key, arguments) ?? key;

    private void PrepareChunkBuffer(byte[] buffer, int chunkIndex)
    {
        // 8 bytes Magic
        Buffer.BlockCopy(MagicHeader, 0, buffer, 0, MagicHeader.Length);

        // 4 bytes Index
        byte[] idxBytes = BitConverter.GetBytes(chunkIndex);
        Buffer.BlockCopy(idxBytes, 0, buffer, 8, 4);

        // Fill the payload with a reproducible pattern without a per-integer loop.
        int seed = chunkIndex * 314159 + 271828;
        new Random(seed).NextBytes(buffer.AsSpan(12));
    }

    private bool ValidateChunkBuffer(byte[] buffer, int expectedChunkIndex, byte[] expectedBuffer)
    {
        PrepareChunkBuffer(expectedBuffer, expectedChunkIndex);
        return buffer.AsSpan().SequenceEqual(expectedBuffer);
    }
}
