using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Logging;
using UsbRecoverySuite.Core.Services.Storage;
using Xunit;

namespace UsbRecoverySuite.Tests;

public class SafetyValidatorTests
{
    private readonly ISafetyValidatorService _validator;

    public SafetyValidatorTests()
    {
        _validator = new SafetyValidatorService(new AppLogger());
    }

    [Fact]
    public void System_Disk_Is_Strictly_Blocked()
    {
        var disk = new UsbDeviceInfo
        {
            DiskIndex = 0,
            Model = "Intel NVMe SSD 512GB",
            BusType = "NVMe",
            IsSystem = true,
            DeviceType = UsbStorageDeviceType.InternalDisk
        };

        bool safe = _validator.ValidateSafeForDestructiveAction(disk, out var reason);
        Assert.False(safe);
        Assert.Contains("BLOCKED", reason);
        Assert.Contains("System Disk", reason);
    }

    [Fact]
    public void Boot_Disk_Is_Strictly_Blocked()
    {
        var disk = new UsbDeviceInfo
        {
            DiskIndex = 1,
            Model = "SATA Boot SSD",
            BusType = "SATA",
            IsBoot = true,
            DeviceType = UsbStorageDeviceType.InternalDisk
        };

        bool safe = _validator.ValidateSafeForDestructiveAction(disk, out var reason);
        Assert.False(safe);
        Assert.Contains("Boot Disk", reason);
    }

    [Fact]
    public void Internal_Disk_Is_Strictly_Blocked()
    {
        var disk = new UsbDeviceInfo
        {
            DiskIndex = 1,
            Model = "Western Digital Black 2TB",
            BusType = "SATA",
            IsRemovable = false,
            DeviceType = UsbStorageDeviceType.InternalDisk
        };

        bool safe = _validator.ValidateSafeForDestructiveAction(disk, out var reason);
        Assert.False(safe);
        Assert.Contains("internal drive", reason);
    }

    [Fact]
    public void Volume_With_C_Drive_Is_Strictly_Blocked()
    {
        var disk = new UsbDeviceInfo
        {
            DiskIndex = 1,
            Model = "Some Disk",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            Volumes = new List<VolumeInfo>
            {
                new VolumeInfo { DriveLetter = "C:", FileSystem = "NTFS" }
            }
        };

        bool safe = _validator.ValidateSafeForDestructiveAction(disk, out var reason);
        Assert.False(safe);
        Assert.Contains("active Windows OS drive", reason);
    }

    [Fact]
    public void Valid_Usb_Flash_Drive_Passes_Safety_Check()
    {
        var disk = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "SanDisk Cruzer Glide",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            IsSystem = false,
            IsBoot = false,
            IsPageFile = false,
            Volumes = new List<VolumeInfo>
            {
                new VolumeInfo { DriveLetter = "E:", FileSystem = "FAT32" }
            }
        };

        bool safe = _validator.ValidateSafeForDestructiveAction(disk, out var reason);
        Assert.True(safe);
        Assert.Empty(reason);
    }

    [Fact]
    public void External_Usb_Ssd_Identified_For_Extra_Caution()
    {
        var disk = new UsbDeviceInfo
        {
            DiskIndex = 3,
            Model = "Samsung Portable SSD T7",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.ExternalUsbDisk,
            IsRemovable = false,
            ReportedCapacityBytes = 1000L * 1024 * 1024 * 1024
        };

        bool requiresCaution = _validator.IsExternalUsbDriveRequiringExtraCaution(disk);
        Assert.True(requiresCaution);
    }

    [Fact]
    public void Disconnected_Device_Fails_Integrity_Check()
    {
        var expected = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "Kingston Flash",
            SerialNumber = "K12345"
        };

        bool intact = _validator.VerifyDeviceIntegrityBeforeAction(expected, null, out var reason);
        Assert.False(intact);
        Assert.Contains("disconnected", reason);
    }

    [Fact]
    public void Serial_Mismatch_Fails_Integrity_Check()
    {
        var expected = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "Kingston Flash",
            SerialNumber = "K12345"
        };

        var current = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "Kingston Flash",
            SerialNumber = "DIFFERENT_999"
        };

        bool intact = _validator.VerifyDeviceIntegrityBeforeAction(expected, current, out var reason);
        Assert.False(intact);
        Assert.Contains("serial number mismatch", reason);
    }
}
