using System.IO;
using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.UI.Services;

public sealed record FileTransferProgress(
    string CurrentFile,
    int CompletedFiles,
    int TotalFiles,
    long CopiedBytes,
    long TotalBytes);

public sealed record FileTransferResult(string DestinationPath, int FileCount, long TotalBytes);

public sealed class UsbFileTransferService
{
    public Task<FileTransferResult> CopyUsbToPcAsync(
        UsbDeviceInfo device,
        string destinationFolder,
        IProgress<FileTransferProgress> progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => CopyUsbToPcCoreAsync(device, destinationFolder, progress, cancellationToken), cancellationToken);

    public Task<FileTransferResult> CopyPcFilesToUsbAsync(
        UsbDeviceInfo device,
        IReadOnlyCollection<string> sourceFiles,
        IProgress<FileTransferProgress> progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => CopyPcFilesToUsbCoreAsync(device, sourceFiles, progress, cancellationToken), cancellationToken);

    public static IReadOnlyList<string> GetDriveRoots(UsbDeviceInfo device) =>
        device.Volumes.Select(volume => volume.DriveLetter)
            .Concat(device.Partitions.SelectMany(partition => partition.DriveLetters))
            .Select(NormalizeDriveRoot)
            .Where(root => root != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<FileTransferResult> CopyUsbToPcCoreAsync(
        UsbDeviceInfo device,
        string destinationFolder,
        IProgress<FileTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var roots = GetDriveRoots(device);
        if (roots.Count == 0)
            throw new IOException("The selected USB drive has no accessible drive letter.");

        var fullDestination = Path.GetFullPath(destinationFolder);
        if (roots.Any(root => IsSameOrChildPath(fullDestination, root)))
            throw new IOException("Choose a PC destination outside the selected USB drive.");

        var backupFolderName = $"LuminaDrive-USB-Disk{device.DiskIndex}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var outputFolder = Path.Combine(fullDestination, backupFolderName);
        for (var suffix = 1; Directory.Exists(outputFolder) || File.Exists(outputFolder); suffix++)
            outputFolder = Path.Combine(fullDestination, $"{backupFolderName}-{suffix}");

        var sources = roots.Select(root => new SourceRoot(root, Path.GetPathRoot(root)!.TrimEnd('\\', ':'))).ToArray();
        var inventory = BuildInventory(sources, cancellationToken);
        Directory.CreateDirectory(outputFolder);

        var totalBytes = inventory.Files.Sum(file => file.Length);
        long copiedBytes = 0;
        var completedFiles = 0;

        foreach (var directory in inventory.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(outputFolder, directory.RelativePath));
        }

        foreach (var file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(outputFolder, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Report(progress, file.SourcePath, completedFiles, inventory.Files.Count, copiedBytes, totalBytes);
            copiedBytes += await CopyFileAsync(file.SourcePath, targetPath, copiedBytes, totalBytes,
                completedFiles, inventory.Files.Count, progress, cancellationToken);
            completedFiles++;
            Report(progress, file.SourcePath, completedFiles, inventory.Files.Count, copiedBytes, totalBytes);
        }

        return new FileTransferResult(outputFolder, inventory.Files.Count, totalBytes);
    }

    private static async Task<FileTransferResult> CopyPcFilesToUsbCoreAsync(
        UsbDeviceInfo device,
        IReadOnlyCollection<string> sourceFiles,
        IProgress<FileTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        if (sourceFiles.Count == 0)
            throw new IOException("Select at least one file to copy.");

        var roots = GetDriveRoots(device);
        if (roots.Count == 0)
            throw new IOException("The selected USB drive has no accessible drive letter.");

        var fullSources = sourceFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (fullSources.Any(path => roots.Any(root => IsSameOrChildPath(path, root))))
            throw new IOException("The selected files are already on this USB drive. Choose files from the PC.");
        foreach (var source in fullSources)
            if (!File.Exists(source))
                throw new FileNotFoundException("A selected PC file could not be found.", source);

        var targetRoot = FindWritableDestinationRoot(roots);
        var totalBytes = fullSources.Sum(path => new FileInfo(path).Length);
        var availableBytes = new DriveInfo(targetRoot).AvailableFreeSpace;
        if (totalBytes > availableBytes)
            throw new IOException($"The USB drive needs {VolumeInfo.FormatBytes(totalBytes)} free, but only {VolumeInfo.FormatBytes(availableBytes)} is available.");

        long copiedBytes = 0;
        var completedFiles = 0;
        foreach (var source in fullSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = GetNonCollidingPath(targetRoot, Path.GetFileName(source));
            Report(progress, source, completedFiles, fullSources.Length, copiedBytes, totalBytes);
            copiedBytes += await CopyFileAsync(source, destination, copiedBytes, totalBytes,
                completedFiles, fullSources.Length, progress, cancellationToken);
            completedFiles++;
            Report(progress, source, completedFiles, fullSources.Length, copiedBytes, totalBytes);
        }

        return new FileTransferResult(targetRoot, fullSources.Length, totalBytes);
    }

    private static Inventory BuildInventory(IReadOnlyList<SourceRoot> roots, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        var directories = new List<SourceDirectory>();
        var multiVolume = roots.Count > 1;

        foreach (var root in roots)
        {
            var relativeRoot = multiVolume ? $"{root.Label}-Drive" : string.Empty;
            var pending = new Stack<(string FullPath, string RelativePath)>();
            pending.Push((root.Path, relativeRoot));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (currentPath, currentRelativePath) = pending.Pop();
                foreach (var directoryPath in Directory.GetDirectories(currentPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0)
                        continue;

                    var relativePath = Path.Combine(currentRelativePath, Path.GetFileName(directoryPath));
                    directories.Add(new SourceDirectory(relativePath));
                    pending.Push((directoryPath, relativePath));
                }

                foreach (var filePath in Directory.GetFiles(currentPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                        continue;

                    var relativePath = Path.Combine(currentRelativePath, Path.GetFileName(filePath));
                    files.Add(new SourceFile(filePath, relativePath, new FileInfo(filePath).Length));
                }
            }
        }

        return new Inventory(files, directories);
    }

    private static string FindWritableDestinationRoot(IReadOnlyList<string> roots)
    {
        return roots.Select(root =>
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    return (Root: root, Free: -1L);

                try
                {
                    return (Root: root, Free: drive.AvailableFreeSpace);
                }
                catch (IOException)
                {
                    return (Root: root, Free: -1L);
                }
            })
            .Where(candidate => candidate.Free >= 0)
            .OrderByDescending(candidate => candidate.Free)
            .Select(candidate => candidate.Root)
            .FirstOrDefault() ?? throw new IOException("No writable volume is available on the selected USB drive.");
    }

    private static async Task<long> CopyFileAsync(
        string source,
        string destination,
        long bytesAlreadyCopied,
        long totalBytes,
        int completedFiles,
        int totalFiles,
        IProgress<FileTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        long fileBytesCopied = 0;
        long lastReportedBytes = 0;

        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                        break;

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    fileBytesCopied += read;
                    if (fileBytesCopied - lastReportedBytes >= 8 * 1024 * 1024)
                    {
                        Report(progress, source, completedFiles, totalFiles, bytesAlreadyCopied + fileBytesCopied, totalBytes);
                        lastReportedBytes = fileBytesCopied;
                    }
                }
            }

            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            return fileBytesCopied;
        }
        catch
        {
            try { File.Delete(destination); }
            catch { /* Preserve the original copy or cancellation error. */ }
            throw;
        }
    }

    private static string GetNonCollidingPath(string destinationRoot, string fileName)
    {
        var candidate = Path.Combine(destinationRoot, fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 1; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(destinationRoot, $"{name} ({suffix}){extension}");
        return candidate;
    }

    private static string? NormalizeDriveRoot(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
            return $"{trimmed}:\\";
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            return $"{trimmed}\\";
        return null;
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(
        IProgress<FileTransferProgress> progress,
        string currentFile,
        int completedFiles,
        int totalFiles,
        long copiedBytes,
        long totalBytes) =>
        progress.Report(new FileTransferProgress(currentFile, completedFiles, totalFiles, copiedBytes, totalBytes));

    private sealed record SourceRoot(string Path, string Label);
    private sealed record SourceFile(string SourcePath, string RelativePath, long Length);
    private sealed record SourceDirectory(string RelativePath);
    private sealed record Inventory(List<SourceFile> Files, List<SourceDirectory> Directories);
}
