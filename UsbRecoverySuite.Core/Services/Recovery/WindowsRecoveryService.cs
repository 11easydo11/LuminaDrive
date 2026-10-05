using System.Diagnostics;
using System.Text.RegularExpressions;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Diagnostics;
using UsbRecoverySuite.Core.Services.Localization;
using UsbRecoverySuite.Core.Services.Logging;
using UsbRecoverySuite.Core.Services.Storage;

namespace UsbRecoverySuite.Core.Services.Recovery;

public class WindowsRecoveryService : IRecoveryService
{
    private readonly IWindowsStorageService _storageService;
    private readonly ISafetyValidatorService _safetyValidator;
    private readonly IDiagnosticEngine _diagnosticEngine;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService? _localization;

    public WindowsRecoveryService(
        IWindowsStorageService storageService,
        ISafetyValidatorService safetyValidator,
        IDiagnosticEngine diagnosticEngine,
        IAppLogger logger,
        ILocalizationService? localization = null)
    {
        _storageService = storageService;
        _safetyValidator = safetyValidator;
        _diagnosticEngine = diagnosticEngine;
        _logger = logger;
        _localization = localization;
    }

    public async Task<RepairOperationResult> CleanAndRebuildDiskAsync(
        UsbDeviceInfo targetDevice,
        PartitionTableType partitionStyle,
        string fileSystem,
        string volumeLabel,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.CleanAndPartitionAll,
            OperationName = "Clean and Rebuild USB Storage",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        try
        {
            progress.Report(Text("RecoveryStepValidate"));
            if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyError))
            {
                result.Success = false;
                result.ErrorMessage = safetyError;
                result.VerificationSummary = safetyError;
                result.StepsExecuted.Add($"Safety check FAILED: {safetyError}");
                return result;
            }
            result.StepsExecuted.Add("Safety validation passed: Device is safe for repartitioning.");

            progress.Report(Text("RecoveryStepIntegrity"));
            var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
            if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
            {
                result.Success = false;
                result.ErrorMessage = mismatchError;
                result.VerificationSummary = mismatchError;
                result.StepsExecuted.Add($"Integrity check FAILED: {mismatchError}");
                return result;
            }
            result.StepsExecuted.Add($"Drive integrity verified: Disk {targetDevice.DiskIndex} ({targetDevice.Model}) is ready.");

            // Choose partition table style
            if (partitionStyle is not (PartitionTableType.MBR or PartitionTableType.GPT))
                return Fail(result, Text("RecoveryInvalidPartitionStyle"));

            string styleCmd = partitionStyle == PartitionTableType.GPT ? "convert gpt" : "convert mbr";
            string fsChoice = string.IsNullOrWhiteSpace(fileSystem) ? "exfat" : fileSystem.Trim().ToLowerInvariant();
            if (fsChoice is not ("exfat" or "ntfs" or "fat32"))
                return Fail(result, Text("RecoveryInvalidFileSystem"));
            if (fsChoice == "fat32" && targetDevice.ReportedCapacityBytes > 32L * 1024 * 1024 * 1024)
                return Fail(result, Text("RecoveryFat32TooLarge"));

            string labelChoice = string.IsNullOrWhiteSpace(volumeLabel) ? "USB_DRIVE" : volumeLabel.Trim();
            int maxLabelLength = fsChoice == "fat32" ? 11 : fsChoice == "exfat" ? 15 : 32;
            if (labelChoice.Length > maxLabelLength || !Regex.IsMatch(labelChoice, @"^[A-Za-z0-9 _-]+$"))
                return Fail(result, Text("RecoveryInvalidVolumeLabel", maxLabelLength));

            progress.Report(Text("RecoveryStepPrepare"));
            var scriptLines = new List<string>
            {
                $"select disk {targetDevice.DiskIndex}",
                "attributes disk clear readonly",
                "clean",
                styleCmd,
                "create partition primary"
            };

            if (partitionStyle == PartitionTableType.MBR)
                scriptLines.Add("active");

            scriptLines.AddRange(new[]
            {
                $"format fs={fsChoice} label=\"{labelChoice}\" quick",
                "assign",
                "rescan",
                "exit"
            });

            progress.Report(Text("RecoveryStepExecute"));
            _logger.LogInfo(nameof(WindowsRecoveryService), $"Executing disk cleaning and re-partitioning on Disk {targetDevice.DiskIndex}...");

            var (dpSuccess, dpOutput) = await RunDiskpartScriptAsync(scriptLines, progress);
            result.StepsExecuted.Add($"Diskpart execution: {(dpSuccess ? "Success" : "Warning/Error")}");

            progress.Report(Text("RecoveryStepRefresh"));
            await Task.Delay(1500);
            await _storageService.RescanStorageBusAsync();

            progress.Report(Text("RecoveryStepRescan"));
            var afterDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
            result.AfterSnapshot = afterDevice?.Clone();

            progress.Report(Text("RecoveryStepVerify"));
            bool verified = false;
            string summary = string.Empty;

            if (afterDevice != null)
            {
                bool hasPartitions = afterDevice.Partitions.Count > 0;
                long partitioned = afterDevice.TotalPartitionedBytes;
                long reported = afterDevice.ReportedCapacityBytes;
                double utilization = reported > 0 ? (double)partitioned / reported * 100.0 : 0.0;

                var afterVol = afterDevice.Volumes.FirstOrDefault() ?? afterDevice.Partitions.FirstOrDefault()?.Volume;
                string afterFs = afterVol?.FileSystem ?? afterDevice.PrimaryFileSystem;
                string afterLetter = afterVol?.DriveLetter ?? afterDevice.DriveLettersDisplay;

                bool expectedFileSystem = afterFs.Equals(fsChoice, StringComparison.OrdinalIgnoreCase);
                if (dpSuccess && hasPartitions && utilization >= 98.0 && expectedFileSystem)
                {
                    verified = true;
                    summary = Text("RecoveryCleanVerified", targetDevice.DiskIndex, utilization,
                        VolumeInfo.FormatBytes(partitioned), VolumeInfo.FormatBytes(reported), afterLetter, afterFs);
                    result.StepsExecuted.Add(summary);
                    result.Success = true;
                }
                else
                {
                    verified = false;
                    summary = Text("RecoveryCleanPartial", afterDevice.Partitions.Count, utilization,
                        VolumeInfo.FormatBytes(partitioned), VolumeInfo.FormatBytes(reported));
                    result.StepsExecuted.Add(summary);
                    result.Success = false;
                    if (!dpSuccess && !string.IsNullOrWhiteSpace(dpOutput))
                        summary += $" DiskPart reported an error: {dpOutput.Trim()}";
                    else if (!expectedFileSystem)
                        summary += $" Expected {fsChoice.ToUpperInvariant()}, found {afterFs}.";
                }
            }
            else
            {
                verified = false;
                summary = Text("RecoveryDeviceMissing");
                result.StepsExecuted.Add(summary);
                result.Success = false;
            }

            result.Verified = verified;
            result.VerificationSummary = summary;
            progress.Report(Text("RecoveryStepFinished", summary));
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.VerificationSummary = ex.Message;
            result.StepsExecuted.Add($"Exception occurred during recovery: {ex.Message}");
            _logger.LogError(nameof(WindowsRecoveryService), $"CleanAndRebuildDiskAsync failed: {ex.Message}");
        }

        return result;
    }

    public async Task<RepairOperationResult> ClearReadOnlyStateAsync(
        UsbDeviceInfo targetDevice,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.ClearReadOnly,
            OperationName = "Clear Read-Only Storage Attribute",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyErr))
        {
            result.Success = false;
            result.ErrorMessage = safetyErr;
            result.VerificationSummary = safetyErr;
            return result;
        }

        var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
        {
            result.ErrorMessage = mismatchError;
            result.VerificationSummary = mismatchError;
            return result;
        }

        progress.Report(Text("RecoveryClearReadOnlyProgress"));
        var script = new List<string>
        {
            $"select disk {targetDevice.DiskIndex}",
            "attributes disk clear readonly",
            "online disk",
            "exit"
        };

        var (success, output) = await RunDiskpartScriptAsync(script, progress);
        await Task.Delay(1000);

        var after = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        result.AfterSnapshot = after?.Clone();
        result.Verified = after != null && !after.IsReadOnly && !after.IsOffline;
        result.Success = success && result.Verified;
        result.VerificationSummary = result.Verified
            ? (result.Success ? Text("RecoveryReadOnlySuccess") : Text("RecoveryReadOnlyCommandFailed"))
            : Text("RecoveryReadOnlyStillLocked");

        return result;
    }

    public async Task<RepairOperationResult> CheckAndRepairFilesystemAsync(
        UsbDeviceInfo targetDevice,
        string driveLetter,
        bool repair,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.CheckAndRepairFilesystem,
            OperationName = repair ? "Repair Filesystem (chkdsk /f)" : "Check Filesystem (chkdsk)",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyError))
        {
            result.ErrorMessage = safetyError;
            result.VerificationSummary = safetyError;
            return result;
        }

        var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
        {
            result.ErrorMessage = mismatchError;
            result.VerificationSummary = mismatchError;
            return result;
        }

        string cleanLetter = driveLetter.TrimEnd('\\', ':');
        if (cleanLetter.Length != 1 || !char.IsLetter(cleanLetter[0]))
        {
            result.Success = false;
            return Fail(result, Text("RecoveryInvalidDriveLetter"));
        }

        string root = $"{cleanLetter}:";
        bool belongsToTarget = targetDevice.Volumes.Any(volume =>
                                   volume.DriveLetter.TrimEnd('\\', ':').Equals(cleanLetter, StringComparison.OrdinalIgnoreCase)) ||
                               targetDevice.Partitions.Any(partition => partition.DriveLetters.Any(letter =>
                                   letter.TrimEnd('\\', ':').Equals(cleanLetter, StringComparison.OrdinalIgnoreCase)));
        if (!belongsToTarget)
        {
            return Fail(result, Text("RecoveryDriveLetterNotOnDisk", cleanLetter, targetDevice.DiskIndex));
        }

        progress.Report(Text(repair ? "RecoveryChkdskRepairProgress" : "RecoveryChkdskCheckProgress", root));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "chkdsk.exe",
                Arguments = repair ? $"{root} /f /x" : $"{root}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    progress.Report(e.Data);
                    result.StepsExecuted.Add(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    progress.Report(e.Data);
                    result.StepsExecuted.Add(e.Data);
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
            proc.WaitForExit();

            result.Success = proc.ExitCode == 0 || proc.ExitCode == 1; // 0=No errors, 1=Errors found and fixed
            result.Verified = result.Success;
            result.VerificationSummary = Text("RecoveryChkdskFinished", proc.ExitCode);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.VerificationSummary = ex.Message;
        }

        var after = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        result.AfterSnapshot = after?.Clone();
        return result;
    }

    public async Task<RepairOperationResult> FormatVolumeAsync(
        UsbDeviceInfo targetDevice,
        string driveLetter,
        string fileSystem,
        string volumeLabel,
        bool quickFormat,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.FormatVolume,
            OperationName = "Format Volume",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyErr))
        {
            result.Success = false;
            result.ErrorMessage = safetyErr;
            result.VerificationSummary = safetyErr;
            return result;
        }

        var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
        {
            result.ErrorMessage = mismatchError;
            result.VerificationSummary = mismatchError;
            return result;
        }

        string cleanLetter = driveLetter.TrimEnd('\\', ':');
        bool belongsToTarget = targetDevice.Volumes.Any(volume =>
                                   volume.DriveLetter.TrimEnd('\\', ':').Equals(cleanLetter, StringComparison.OrdinalIgnoreCase)) ||
                               targetDevice.Partitions.Any(partition => partition.DriveLetters.Any(letter =>
                                   letter.TrimEnd('\\', ':').Equals(cleanLetter, StringComparison.OrdinalIgnoreCase)));
        if (!belongsToTarget)
        {
            return Fail(result, Text("RecoveryDriveLetterNotOnDisk", cleanLetter, targetDevice.DiskIndex));
        }

        string fs = string.IsNullOrWhiteSpace(fileSystem) ? "exfat" : fileSystem.ToLowerInvariant();
        string label = string.IsNullOrWhiteSpace(volumeLabel) ? "USB_DRIVE" : volumeLabel.Trim();
        if (fs is not ("exfat" or "ntfs" or "fat32"))
        {
            return Fail(result, Text("RecoveryInvalidFileSystem"));
        }
        if (fs == "fat32" && targetDevice.ReportedCapacityBytes > 32L * 1024 * 1024 * 1024)
        {
            return Fail(result, Text("RecoveryFat32TooLarge"));
        }
        int maxLabelLength = fs == "fat32" ? 11 : fs == "exfat" ? 15 : 32;
        if (label.Length > maxLabelLength || !Regex.IsMatch(label, @"^[A-Za-z0-9 _-]+$"))
        {
            return Fail(result, Text("RecoveryInvalidVolumeLabel", maxLabelLength));
        }
        string quick = quickFormat ? "quick" : "";

        progress.Report(Text("RecoveryFormatProgress", cleanLetter, fs.ToUpperInvariant(), label));

        var script = new List<string>
        {
            $"select volume={cleanLetter}",
            $"format fs={fs} label=\"{label}\" {quick}",
            "exit"
        };

        var (success, output) = await RunDiskpartScriptAsync(script, progress);
        await Task.Delay(1000);

        var after = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        result.AfterSnapshot = after?.Clone();
        var vol = after?.Volumes.FirstOrDefault(v => v.DriveLetter.StartsWith(cleanLetter, StringComparison.OrdinalIgnoreCase));
        result.Verified = vol != null && vol.FileSystem.Equals(fs, StringComparison.OrdinalIgnoreCase);
        result.Success = success && result.Verified;
        result.VerificationSummary = result.Verified
            ? (result.Success
                ? Text("RecoveryFormatSuccess", cleanLetter, fs.ToUpperInvariant())
                : Text("RecoveryFormatFailed"))
            : Text("RecoveryFormatUnverified");

        return result;
    }

    public async Task<RepairOperationResult> AssignDriveLetterAsync(
        UsbDeviceInfo targetDevice,
        int partitionNumber,
        string? preferredLetter,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.AssignDriveLetter,
            OperationName = "Assign Drive Letter",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyError))
            return Fail(result, safetyError);

        var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
            return Fail(result, mismatchError);
        if (!targetDevice.Partitions.Any(p => p.PartitionNumber == partitionNumber))
            return Fail(result, Text("RecoveryManualActionRequired"));

        progress.Report(Text("RecoveryAssignLetterProgress", partitionNumber));

        string assignCmd = !string.IsNullOrWhiteSpace(preferredLetter) && char.IsLetter(preferredLetter[0])
            ? $"assign letter={preferredLetter[0]}"
            : "assign";

        var script = new List<string>
        {
            $"select disk {targetDevice.DiskIndex}",
            $"select partition {partitionNumber}",
            assignCmd,
            "exit"
        };

        var (success, _) = await RunDiskpartScriptAsync(script, progress);
        await Task.Delay(1000);

        var after = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        result.AfterSnapshot = after?.Clone();
        result.Verified = after?.Partitions.FirstOrDefault(p => p.PartitionNumber == partitionNumber)?.DriveLetters.Count > 0;
        result.Success = success && result.Verified;
        result.VerificationSummary = result.Success
            ? Text("RecoveryAssignLetterSuccess", partitionNumber)
            : Text("RecoveryAssignLetterUnverified", partitionNumber);

        return result;
    }

    public async Task<RepairOperationResult> RemoveDriveLetterAsync(
        UsbDeviceInfo targetDevice,
        string driveLetter,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.RemoveDriveLetter,
            OperationName = "Remove Drive Letter",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        string letter = driveLetter.TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsLetter(letter[0]))
            return Fail(result, Text("RecoveryInvalidDriveLetter"));
        if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyError))
            return Fail(result, safetyError);

        var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
            return Fail(result, mismatchError);
        bool belongsToTarget = targetDevice.Volumes.Any(volume =>
                                   volume.DriveLetter.TrimEnd('\\', ':').Equals(letter, StringComparison.OrdinalIgnoreCase)) ||
                               targetDevice.Partitions.Any(partition => partition.DriveLetters.Any(value =>
                                   value.TrimEnd('\\', ':').Equals(letter, StringComparison.OrdinalIgnoreCase)));
        if (!belongsToTarget)
            return Fail(result, Text("RecoveryDriveLetterNotOnDisk", letter, targetDevice.DiskIndex));

        progress.Report(Text("RecoveryRemoveLetterProgress", letter));

        var script = new List<string>
        {
            $"select volume={letter}",
            $"remove letter={letter}",
            "exit"
        };

        var (success, _) = await RunDiskpartScriptAsync(script, progress);
        await Task.Delay(1000);

        var after = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        result.AfterSnapshot = after?.Clone();
        result.Verified = after != null &&
            !after.Volumes.Any(volume => volume.DriveLetter.TrimEnd('\\', ':').Equals(letter, StringComparison.OrdinalIgnoreCase)) &&
            !after.Partitions.Any(partition => partition.DriveLetters.Any(value =>
                value.TrimEnd('\\', ':').Equals(letter, StringComparison.OrdinalIgnoreCase)));
        result.Success = success && result.Verified;
        result.VerificationSummary = result.Success
            ? Text("RecoveryRemoveLetterSuccess", letter)
            : Text("RecoveryRemoveLetterUnverified", letter);

        return result;
    }

    public async Task<RepairOperationResult> DeletePartitionAsync(
        UsbDeviceInfo targetDevice,
        int partitionNumber,
        IProgress<string> progress)
    {
        var result = new RepairOperationResult
        {
            ActionType = RepairActionType.DeletePartition,
            OperationName = $"Delete Partition #{partitionNumber}",
            TargetDiskIndex = targetDevice.DiskIndex,
            TargetDiskModel = targetDevice.Model,
            BeforeSnapshot = targetDevice.Clone()
        };

        if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out var safetyErr))
        {
            return Fail(result, safetyErr);
        }

        var currentDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(targetDevice, currentDevice, out var mismatchError))
            return Fail(result, mismatchError);
        if (!targetDevice.Partitions.Any(p => p.PartitionNumber == partitionNumber))
            return Fail(result, Text("RecoveryManualActionRequired"));

        progress.Report(Text("RecoveryDeletePartitionProgress", partitionNumber, targetDevice.DiskIndex));

        var script = new List<string>
        {
            $"select disk {targetDevice.DiskIndex}",
            $"select partition {partitionNumber}",
            "delete partition override",
            "exit"
        };

        var (success, _) = await RunDiskpartScriptAsync(script, progress);
        await Task.Delay(1000);

        var after = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
        result.AfterSnapshot = after?.Clone();
        result.Verified = after != null && after.Partitions.All(p => p.PartitionNumber != partitionNumber);
        result.Success = success && result.Verified;
        result.VerificationSummary = result.Success
            ? Text("RecoveryDeletePartitionSuccess", partitionNumber)
            : Text("RecoveryDeletePartitionUnverified", partitionNumber);

        return result;
    }

    public Task<RepairOperationResult> FixAllAsync(
        UsbDeviceInfo targetDevice,
        IProgress<string> progress) => FixAllCoreAsync(targetDevice, progress, 0);

    private async Task<RepairOperationResult> FixAllCoreAsync(
        UsbDeviceInfo targetDevice,
        IProgress<string> progress,
        int repairDepth)
    {
        if (repairDepth > 8)
        {
            return new RepairOperationResult
            {
                Success = false,
                Verified = false,
                ActionType = RepairActionType.FixAll,
                OperationName = "Fix All Diagnostic Repair",
                TargetDiskIndex = targetDevice.DiskIndex,
                TargetDiskModel = targetDevice.Model,
                BeforeSnapshot = targetDevice.Clone(),
                ErrorMessage = Text("RecoveryManualActionRequired"),
                VerificationSummary = Text("RecoveryManualActionRequired")
            };
        }

        progress.Report(Text("RecoveryFixAllStarting"));

        // 1. Diagnose current state
        var diagnostics = await _diagnosticEngine.RunFullDiagnosticsAsync(targetDevice);
        var autoFixes = diagnostics.Where(d => d.CanAutoFix).ToList();

        if (autoFixes.Count == 0)
        {
            return new RepairOperationResult
            {
                Success = true,
                ActionType = RepairActionType.FixAll,
                OperationName = "Fix All Diagnostic Repair",
                TargetDiskIndex = targetDevice.DiskIndex,
                TargetDiskModel = targetDevice.Model,
                BeforeSnapshot = targetDevice.Clone(),
                AfterSnapshot = targetDevice.Clone(),
                Verified = true,
                VerificationSummary = Text("RecoveryNoActionNeeded")
            };
        }

        // If partition structure is broken or has unallocated space, clean and rebuild is optimal
        if (autoFixes.Any(f => f.FixType == RepairActionType.CleanAndPartitionAll))
        {
            // Choose GPT for >2TB, MBR for <= 2TB or general flash
            var style = targetDevice.ReportedCapacityBytes > 2L * 1024 * 1024 * 1024 * 1024
                ? PartitionTableType.GPT
                : PartitionTableType.MBR;

            // Choose exFAT for flash, NTFS for external HDD/SSD
            string fs = targetDevice.DeviceType == UsbStorageDeviceType.ExternalUsbDisk ? "ntfs" : "exfat";

            var rebuilt = await CleanAndRebuildDiskAsync(targetDevice, style, fs, "USB_RECOVERED", progress);
            rebuilt.ActionType = RepairActionType.FixAll;
            rebuilt.OperationName = "Fix All Diagnostic Repair";
            return rebuilt;
        }

        // Bring an offline or read-only disk back online before addressing follow-up issues.
        if (autoFixes.Any(f => f.FixType is RepairActionType.ClearReadOnly or RepairActionType.FixAll))
        {
            var accessResult = await ClearReadOnlyStateAsync(targetDevice, progress);
            if (!accessResult.Success || !accessResult.Verified)
                return accessResult;

            var refreshedDevice = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
            if (refreshedDevice == null)
            {
                accessResult.Success = false;
                accessResult.Verified = false;
                accessResult.VerificationSummary = Text("RecoveryDeviceMissing");
                return accessResult;
            }

            var remainingFixes = (await _diagnosticEngine.RunFullDiagnosticsAsync(refreshedDevice))
                .Where(d => d.CanAutoFix)
                .ToList();
            if (remainingFixes.Count > 0)
            {
                var followUp = await FixAllCoreAsync(refreshedDevice, progress, repairDepth + 1);
                followUp.BeforeSnapshot = targetDevice.Clone();
                followUp.OperationName = "Fix All Diagnostic Repair";
                followUp.ActionType = RepairActionType.FixAll;
                if (followUp.Success && followUp.Verified &&
                    followUp.VerificationSummary.Equals(Text("RecoveryNoActionNeeded"), StringComparison.Ordinal))
                    followUp.VerificationSummary = Text("RecoveryFixAllVerified");
                followUp.StepsExecuted.InsertRange(0, accessResult.StepsExecuted);
                return followUp;
            }

            accessResult.ActionType = RepairActionType.FixAll;
            accessResult.OperationName = "Fix All Diagnostic Repair";
            accessResult.BeforeSnapshot = targetDevice.Clone();
            accessResult.AfterSnapshot = refreshedDevice.Clone();
            accessResult.VerificationSummary = Text("RecoveryFixAllVerified");
            return accessResult;
        }

        var formatFix = autoFixes.FirstOrDefault(f => f.FixType == RepairActionType.FormatVolume);
        var assignFix = autoFixes.FirstOrDefault(f => f.FixType == RepairActionType.AssignDriveLetter);
        if (assignFix != null)
        {
            var partitions = targetDevice.Partitions.Where(p =>
                p.DriveLetters.Count == 0 && p.PartitionType.Contains("Basic", StringComparison.OrdinalIgnoreCase)).ToList();
            if (partitions.Count == 0)
            {
                return CreateFixAllFailure(targetDevice, "RecoveryManualActionRequired");
            }

            var assignment = new RepairOperationResult
            {
                Success = true,
                Verified = true,
                ActionType = RepairActionType.FixAll,
                OperationName = "Fix All Diagnostic Repair",
                TargetDiskIndex = targetDevice.DiskIndex,
                TargetDiskModel = targetDevice.Model,
                BeforeSnapshot = targetDevice.Clone()
            };
            foreach (var partition in partitions)
            {
                var assigned = await AssignDriveLetterAsync(targetDevice, partition.PartitionNumber, null, progress);
                assignment.StepsExecuted.AddRange(assigned.StepsExecuted);
                if (!assigned.Success || !assigned.Verified)
                {
                    assigned.ActionType = RepairActionType.FixAll;
                    assigned.OperationName = "Fix All Diagnostic Repair";
                    assigned.BeforeSnapshot = targetDevice.Clone();
                    return assigned;
                }
            }

            var afterAssignment = await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex);
            if (afterAssignment == null)
                return CreateFixAllFailure(targetDevice, "RecoveryDriveLetterUnverified");

            var followUp = await FixAllCoreAsync(afterAssignment, progress, repairDepth + 1);
            followUp.BeforeSnapshot = targetDevice.Clone();
            followUp.ActionType = RepairActionType.FixAll;
            followUp.OperationName = "Fix All Diagnostic Repair";
            if (followUp.Success && followUp.Verified &&
                followUp.VerificationSummary.Equals(Text("RecoveryNoActionNeeded"), StringComparison.Ordinal))
                followUp.VerificationSummary = Text("RecoveryFixAllVerified");
            followUp.StepsExecuted.InsertRange(0, assignment.StepsExecuted);
            return followUp;
        }

        if (formatFix != null)
        {
            var partitionNeedingFormat = targetDevice.Partitions.FirstOrDefault(p => p.Volume != null &&
                (p.Volume.FileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase) ||
                 p.Volume.FileSystem.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                 (p.Volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) &&
                  p.SizeBytes > 32L * 1024 * 1024 * 1024)));
            var vol = partitionNeedingFormat?.Volume;
            if (vol != null && !string.IsNullOrWhiteSpace(vol.DriveLetter))
            {
                var formatted = await FormatVolumeAsync(targetDevice, vol.DriveLetter, "exfat", "USB_DRIVE", true, progress);
                if (!formatted.Success || !formatted.Verified || formatted.AfterSnapshot == null)
                    return formatted;

                var followUp = await FixAllCoreAsync(formatted.AfterSnapshot, progress, repairDepth + 1);
                followUp.BeforeSnapshot = targetDevice.Clone();
                followUp.ActionType = RepairActionType.FixAll;
                followUp.OperationName = "Fix All Diagnostic Repair";
                if (followUp.Success && followUp.Verified &&
                    followUp.VerificationSummary.Equals(Text("RecoveryNoActionNeeded"), StringComparison.Ordinal))
                    followUp.VerificationSummary = Text("RecoveryFixAllVerified");
                followUp.StepsExecuted.InsertRange(0, formatted.StepsExecuted);
                return followUp;
            }
        }

        // Never erase a disk just because an automatic repair path is missing.
        return CreateFixAllFailure(targetDevice, "RecoveryManualActionRequired");
    }

    private RepairOperationResult CreateFixAllFailure(UsbDeviceInfo targetDevice, string messageKey) => new()
    {
        Success = false,
        Verified = false,
        ActionType = RepairActionType.FixAll,
        OperationName = "Fix All Diagnostic Repair",
        TargetDiskIndex = targetDevice.DiskIndex,
        TargetDiskModel = targetDevice.Model,
        BeforeSnapshot = targetDevice.Clone(),
        ErrorMessage = Text(messageKey),
        VerificationSummary = Text(messageKey)
    };

    #region Diskpart Helper

    private static RepairOperationResult Fail(RepairOperationResult result, string message)
    {
        result.Success = false;
        result.Verified = false;
        result.ErrorMessage = message;
        result.VerificationSummary = message;
        return result;
    }

    private string Text(string key, params object[] arguments) =>
        _localization?.Get(key, arguments) ?? key;

    private async Task<(bool Success, string Output)> RunDiskpartScriptAsync(List<string> scriptLines, IProgress<string> progress)
    {
        string tempScript = Path.Combine(Path.GetTempPath(), $"dp_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(tempScript, scriptLines);

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{tempScript}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            var outputLines = new List<string>();

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    outputLines.Add(e.Data);
                    progress.Report(e.Data);
                    _logger.LogDebug(nameof(WindowsRecoveryService), $"[diskpart] {e.Data}");
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync();
            proc.WaitForExit();

            string fullOutput = string.Join(Environment.NewLine, outputLines);
            string[] errorMarkers =
            {
                "DiskPart has encountered an error",
                "Virtual Disk Service error",
                "The arguments specified for this command are not valid",
                "The system cannot find the file specified",
                "Ошибка службы виртуальных дисков",
                "DiskPart обнаружила ошибку",
                "Указанные для команды аргументы недопустимы"
            };
            bool success = proc.ExitCode == 0 && !errorMarkers.Any(marker =>
                fullOutput.Contains(marker, StringComparison.OrdinalIgnoreCase));

            return (success, fullOutput);
        }
        finally
        {
            try
            {
                if (File.Exists(tempScript)) File.Delete(tempScript);
            }
            catch { }
        }
    }

    #endregion
}
