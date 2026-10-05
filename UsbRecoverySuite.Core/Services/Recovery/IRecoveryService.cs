using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Recovery;

public interface IRecoveryService
{
    Task<RepairOperationResult> CleanAndRebuildDiskAsync(
        UsbDeviceInfo targetDevice,
        PartitionTableType partitionStyle,
        string fileSystem,
        string volumeLabel,
        IProgress<string> progress);

    Task<RepairOperationResult> ClearReadOnlyStateAsync(
        UsbDeviceInfo targetDevice,
        IProgress<string> progress);

    Task<RepairOperationResult> CheckAndRepairFilesystemAsync(
        UsbDeviceInfo targetDevice,
        string driveLetter,
        bool repair,
        IProgress<string> progress);

    Task<RepairOperationResult> FormatVolumeAsync(
        UsbDeviceInfo targetDevice,
        string driveLetter,
        string fileSystem,
        string volumeLabel,
        bool quickFormat,
        IProgress<string> progress);

    Task<RepairOperationResult> AssignDriveLetterAsync(
        UsbDeviceInfo targetDevice,
        int partitionNumber,
        string? preferredLetter,
        IProgress<string> progress);

    Task<RepairOperationResult> RemoveDriveLetterAsync(
        UsbDeviceInfo targetDevice,
        string driveLetter,
        IProgress<string> progress);

    Task<RepairOperationResult> DeletePartitionAsync(
        UsbDeviceInfo targetDevice,
        int partitionNumber,
        IProgress<string> progress);

    Task<RepairOperationResult> FixAllAsync(
        UsbDeviceInfo targetDevice,
        IProgress<string> progress);
}
