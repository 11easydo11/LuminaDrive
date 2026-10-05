using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Logging;

namespace UsbRecoverySuite.Core.Services.Storage;

public class SafetyValidatorService : ISafetyValidatorService
{
    private readonly IAppLogger _logger;
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string? WindowsDriveLetter = Path.GetPathRoot(WindowsDirectory)?.TrimEnd('\\').ToUpperInvariant();

    // Standard GPT GUIDs
    private const string GptEfiSystemPartitionGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    private const string GptMsftRecoveryPartitionGuid = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

    public SafetyValidatorService(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool ValidateSafeForDestructiveAction(UsbDeviceInfo device, out string failureReason)
    {
        failureReason = string.Empty;

        if (device == null)
        {
            failureReason = "No device specified.";
            _logger.LogError(nameof(SafetyValidatorService), failureReason);
            return false;
        }

        // 1. Is this disk identified as the Windows System Disk?
        if (device.IsSystem)
        {
            failureReason = $"BLOCKED: Disk {device.DiskIndex} is marked as the Windows System Disk. Destructive operations are strictly prohibited.";
            _logger.LogError(nameof(SafetyValidatorService), failureReason);
            return false;
        }

        // 2. Is this disk identified as the Boot Disk?
        if (device.IsBoot)
        {
            failureReason = $"BLOCKED: Disk {device.DiskIndex} is marked as the Windows Boot Disk. Destructive operations are strictly prohibited.";
            _logger.LogError(nameof(SafetyValidatorService), failureReason);
            return false;
        }

        // 3. Does it host the Windows Pagefile / Crash Dump?
        if (device.IsPageFile)
        {
            failureReason = $"BLOCKED: Disk {device.DiskIndex} contains active Windows paging or crash dump files.";
            _logger.LogError(nameof(SafetyValidatorService), failureReason);
            return false;
        }

        // 4. Is it classified as an Internal Disk?
        if (device.DeviceType == UsbStorageDeviceType.InternalDisk)
        {
            failureReason = $"BLOCKED: Disk {device.DiskIndex} is an internal drive ({device.Model}, Bus: {device.BusType}). Only removable or external USB drives can be repaired.";
            _logger.LogError(nameof(SafetyValidatorService), failureReason);
            return false;
        }

        // 5. Check BusType explicitly
        var bus = (device.BusType ?? string.Empty).Trim().ToUpperInvariant();
        if (bus != "USB" && bus != "UASP" && bus != "MMC" && bus != "SD" && !device.IsRemovable)
        {
            failureReason = $"BLOCKED: Disk {device.DiskIndex} uses a non-USB/non-removable bus interface ('{device.BusType}').";
            _logger.LogError(nameof(SafetyValidatorService), failureReason);
            return false;
        }

        // 6. Check Volumes for System Drive letter (C:) or Windows folder
        foreach (var vol in device.Volumes)
        {
            if (vol.IsSystem || vol.IsBoot)
            {
                failureReason = $"BLOCKED: Volume '{vol.DriveLetter}' on Disk {device.DiskIndex} is marked as System or Boot.";
                _logger.LogError(nameof(SafetyValidatorService), failureReason);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(WindowsDriveLetter) && 
                !string.IsNullOrWhiteSpace(vol.DriveLetter) &&
                string.Equals(vol.DriveLetter.TrimEnd('\\'), WindowsDriveLetter, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = $"BLOCKED: Disk {device.DiskIndex} hosts the active Windows OS drive letter ({WindowsDriveLetter}).";
                _logger.LogError(nameof(SafetyValidatorService), failureReason);
                return false;
            }
        }

        // 7. Check for EFI / Recovery partitions on non-flash devices
        foreach (var part in device.Partitions)
        {
            if (!string.IsNullOrWhiteSpace(part.GptTypeGuid))
            {
                var guid = part.GptTypeGuid.ToLowerInvariant();
                if (guid == GptEfiSystemPartitionGuid && !device.IsRemovable)
                {
                    failureReason = $"BLOCKED: Disk {device.DiskIndex} contains an active EFI System Partition.";
                    _logger.LogError(nameof(SafetyValidatorService), failureReason);
                    return false;
                }
            }
        }

        _logger.LogInfo(nameof(SafetyValidatorService), $"Safety validation PASSED for Disk {device.DiskIndex} ({device.Model}).");
        return true;
    }

    public bool VerifyDeviceIntegrityBeforeAction(UsbDeviceInfo expectedDevice, UsbDeviceInfo? currentDevice, out string mismatchReason)
    {
        mismatchReason = string.Empty;

        if (currentDevice == null)
        {
            mismatchReason = $"CRITICAL ERROR: Disk {expectedDevice.DiskIndex} was disconnected or is no longer detected by Windows!";
            _logger.LogError(nameof(SafetyValidatorService), mismatchReason);
            return false;
        }

        if (currentDevice.DiskIndex != expectedDevice.DiskIndex)
        {
            mismatchReason = $"CRITICAL ERROR: Disk index changed from {expectedDevice.DiskIndex} to {currentDevice.DiskIndex}. Operation aborted.";
            _logger.LogError(nameof(SafetyValidatorService), mismatchReason);
            return false;
        }

        // Check model match if not placeholder
        if (expectedDevice.Model != "Not available" && currentDevice.Model != "Not available")
        {
            if (!string.Equals(expectedDevice.Model, currentDevice.Model, StringComparison.OrdinalIgnoreCase))
            {
                mismatchReason = $"CRITICAL ERROR: Disk model mismatch! Expected '{expectedDevice.Model}', but detected '{currentDevice.Model}'. Device may have changed!";
                _logger.LogError(nameof(SafetyValidatorService), mismatchReason);
                return false;
            }
        }

        // Check serial number if available
        if (expectedDevice.SerialNumber != "Not available" && currentDevice.SerialNumber != "Not available")
        {
            if (!string.Equals(expectedDevice.SerialNumber.Trim(), currentDevice.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                mismatchReason = $"CRITICAL ERROR: Device serial number mismatch! Expected '{expectedDevice.SerialNumber}', but found '{currentDevice.SerialNumber}'.";
                _logger.LogError(nameof(SafetyValidatorService), mismatchReason);
                return false;
            }
        }

        // Validate that capacity hasn't completely drifted (unless disk was uninitialized)
        if (expectedDevice.ReportedCapacityBytes > 0 && currentDevice.ReportedCapacityBytes > 0)
        {
            long diff = Math.Abs(expectedDevice.ReportedCapacityBytes - currentDevice.ReportedCapacityBytes);
            if (diff > 1024 * 1024 * 1024L) // > 1GB difference
            {
                mismatchReason = $"CRITICAL ERROR: Device reported capacity changed significantly ({VolumeInfo.FormatBytes(expectedDevice.ReportedCapacityBytes)} -> {VolumeInfo.FormatBytes(currentDevice.ReportedCapacityBytes)}).";
                _logger.LogError(nameof(SafetyValidatorService), mismatchReason);
                return false;
            }
        }

        return true;
    }

    public bool IsExternalUsbDriveRequiringExtraCaution(UsbDeviceInfo device)
    {
        return device.DeviceType == UsbStorageDeviceType.ExternalUsbDisk;
    }
}
