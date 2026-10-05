using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Storage;

public interface ISafetyValidatorService
{
    /// <summary>
    /// Checks whether the specified disk is completely safe to undergo destructive operations (repartitioning, cleaning, formatting).
    /// Prevents destruction of Windows System, Boot, PageFile, CrashDump, Internal drives, and protected partitions.
    /// </summary>
    bool ValidateSafeForDestructiveAction(UsbDeviceInfo device, out string failureReason);

    /// <summary>
    /// Verifies that a target disk snapshot still matches the currently connected physical drive
    /// (same index, model, and serial number) and is still present.
    /// </summary>
    bool VerifyDeviceIntegrityBeforeAction(UsbDeviceInfo expectedDevice, UsbDeviceInfo? currentDevice, out string mismatchReason);

    /// <summary>
    /// Checks if device is an external USB SSD or HDD (which requires extra caution and distinct warnings).
    /// </summary>
    bool IsExternalUsbDriveRequiringExtraCaution(UsbDeviceInfo device);
}
