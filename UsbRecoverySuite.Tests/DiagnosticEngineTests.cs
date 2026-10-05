using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Diagnostics;
using UsbRecoverySuite.Core.Services.Logging;
using UsbRecoverySuite.Core.Services.Storage;
using Xunit;

namespace UsbRecoverySuite.Tests;

public class DiagnosticEngineTests
{
    private readonly IDiagnosticEngine _diagnosticEngine;
    private readonly ISafetyValidatorService _safetyValidator;
    private readonly IAppLogger _logger;

    public DiagnosticEngineTests()
    {
        _logger = new AppLogger();
        var eventLogService = new MockEventLogService();
        _diagnosticEngine = new DiagnosticEngine(eventLogService, _logger);
        _safetyValidator = new SafetyValidatorService(_logger);
    }

    [Fact]
    public async Task Scenario_16GB_Usb_With_600MB_Partition_Detects_Lost_Space_And_Recommends_Fix()
    {
        // Arrange: 16 GB physical USB with only 600 MB partition and ~15.4 GB unallocated
        long sixteenGb = 16L * 1024 * 1024 * 1024;
        long sixHundredMb = 600L * 1024 * 1024;

        var device = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "SanDisk Ultra USB 3.0",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            ReportedCapacityBytes = sixteenGb,
            PartitionStyle = PartitionTableType.MBR,
            Partitions = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    DiskIndex = 2,
                    PartitionNumber = 1,
                    SizeBytes = sixHundredMb,
                    StartingOffset = 1048576,
                    PartitionType = "Basic data partition",
                    DriveLetters = new List<string> { "F:" },
                    Volume = new VolumeInfo
                    {
                        DriveLetter = "F:",
                        VolumeLabel = "BOOTABLE",
                        FileSystem = "FAT32",
                        TotalBytes = sixHundredMb,
                        FreeBytes = 200L * 1024 * 1024
                    }
                }
            }
        };

        // Act
        var results = await _diagnosticEngine.RunFullDiagnosticsAsync(device);
        var health = _diagnosticEngine.DetermineOverallHealth(results);

        // Assert
        Assert.Equal(HealthStatus.Error, health);

        var unallocatedFinding = results.FirstOrDefault(r => r.Title.Contains("Unallocated Space"));
        Assert.NotNull(unallocatedFinding);
        Assert.Equal(HealthStatus.Error, unallocatedFinding.Severity);
        Assert.True(unallocatedFinding.CanAutoFix);
        Assert.Equal(RepairActionType.CleanAndPartitionAll, unallocatedFinding.FixType);

        var smallPartitionFinding = results.FirstOrDefault(r => r.Title.Contains("Abnormally Small"));
        Assert.NotNull(smallPartitionFinding);
    }

    [Fact]
    public async Task Scenario_Normal_Fully_Used_Usb_Reports_Healthy()
    {
        // Arrange: 32 GB USB with full partition
        long thirtyTwoGb = 32L * 1024 * 1024 * 1024;
        var device = new UsbDeviceInfo
        {
            DiskIndex = 3,
            Model = "Kingston DataTraveler",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            ReportedCapacityBytes = thirtyTwoGb,
            PartitionStyle = PartitionTableType.MBR,
            Partitions = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    DiskIndex = 3,
                    PartitionNumber = 1,
                    SizeBytes = thirtyTwoGb - 2 * 1024 * 1024,
                    StartingOffset = 1048576,
                    DriveLetters = new List<string> { "G:" },
                    Volume = new VolumeInfo
                    {
                        DriveLetter = "G:",
                        VolumeLabel = "DATA",
                        FileSystem = "exFAT",
                        TotalBytes = thirtyTwoGb - 2 * 1024 * 1024,
                        FreeBytes = 20L * 1024 * 1024 * 1024
                    }
                }
            }
        };

        // Act
        var results = await _diagnosticEngine.RunFullDiagnosticsAsync(device);
        var health = _diagnosticEngine.DetermineOverallHealth(results);

        // Assert
        Assert.Equal(HealthStatus.Ok, health);
        Assert.Empty(results.Where(r => r.Severity >= HealthStatus.Warning));
    }

    [Fact]
    public async Task Scenario_Multiple_Partitions_On_Usb_Flash_Detected()
    {
        // Arrange: Flash drive with 3 partitions (e.g. Linux ISO live stick)
        var device = new UsbDeviceInfo
        {
            DiskIndex = 4,
            Model = "Transcend JetFlash",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            ReportedCapacityBytes = 32L * 1024 * 1024 * 1024,
            PartitionStyle = PartitionTableType.MBR,
            Partitions = new List<PartitionInfo>
            {
                new PartitionInfo { DiskIndex = 4, PartitionNumber = 1, SizeBytes = 4L * 1024 * 1024 * 1024 },
                new PartitionInfo { DiskIndex = 4, PartitionNumber = 2, SizeBytes = 500L * 1024 * 1024 },
                new PartitionInfo { DiskIndex = 4, PartitionNumber = 3, SizeBytes = 100L * 1024 * 1024 }
            }
        };

        // Act
        var results = await _diagnosticEngine.RunFullDiagnosticsAsync(device);

        // Assert
        var multiPart = results.FirstOrDefault(r => r.Title.Contains("Multiple Partitions"));
        Assert.NotNull(multiPart);
        Assert.Equal(HealthStatus.Warning, multiPart.Severity);
        Assert.True(multiPart.CanAutoFix);
        Assert.Equal(RepairActionType.CleanAndPartitionAll, multiPart.FixType);
    }

    [Fact]
    public async Task Scenario_Raw_Filesystem_Detected_With_Format_Recommendation()
    {
        // Arrange: Flash drive with RAW volume
        long size = 16L * 1024 * 1024 * 1024;
        var device = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "Generic USB Flash",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            ReportedCapacityBytes = size,
            PartitionStyle = PartitionTableType.MBR,
            Partitions = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    DiskIndex = 2,
                    PartitionNumber = 1,
                    SizeBytes = size - 1048576,
                    StartingOffset = 1048576,
                    DriveLetters = new List<string> { "H:" },
                    Volume = new VolumeInfo
                    {
                        DriveLetter = "H:",
                        FileSystem = "RAW",
                        TotalBytes = size - 1048576
                    }
                }
            }
        };

        // Act
        var results = await _diagnosticEngine.RunFullDiagnosticsAsync(device);

        // Assert
        var rawResult = results.FirstOrDefault(r => r.Title.Contains("RAW / Corrupted Filesystem"));
        Assert.NotNull(rawResult);
        Assert.Equal(HealthStatus.Error, rawResult.Severity);
        Assert.Equal(RepairActionType.FormatVolume, rawResult.FixType);
    }

    [Fact]
    public async Task Scenario_ReadOnly_Drive_Detected_With_ClearReadOnly_Recommendation()
    {
        // Arrange
        var device = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "Protected USB",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            IsReadOnly = true,
            ReportedCapacityBytes = 8L * 1024 * 1024 * 1024,
            PartitionStyle = PartitionTableType.MBR
        };

        // Act
        var results = await _diagnosticEngine.RunFullDiagnosticsAsync(device);

        // Assert
        var roResult = results.FirstOrDefault(r => r.Title.Contains("Read-Only"));
        Assert.NotNull(roResult);
        Assert.Equal(HealthStatus.Error, roResult.Severity);
        Assert.Equal(RepairActionType.ClearReadOnly, roResult.FixType);
    }

    [Fact]
    public async Task Scenario_Uninitialized_Disk_Detected()
    {
        // Arrange
        var device = new UsbDeviceInfo
        {
            DiskIndex = 5,
            Model = "Uninitialized USB",
            BusType = "USB",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            IsRemovable = true,
            ReportedCapacityBytes = 16L * 1024 * 1024 * 1024,
            PartitionStyle = PartitionTableType.RAW,
            Partitions = new List<PartitionInfo>()
        };

        // Act
        var results = await _diagnosticEngine.RunFullDiagnosticsAsync(device);

        // Assert
        var tableResult = results.FirstOrDefault(r => r.Title.Contains("Uninitialized or Corrupted"));
        Assert.NotNull(tableResult);
        Assert.Equal(HealthStatus.Error, tableResult.Severity);
        Assert.Equal(RepairActionType.CleanAndPartitionAll, tableResult.FixType);
    }
}

public class MockEventLogService : IWindowsEventLogService
{
    public Task<List<string>> GetDiskRelatedEventsAsync(int diskIndex, TimeSpan? timeSpan = null)
    {
        return Task.FromResult(new List<string>());
    }
}
