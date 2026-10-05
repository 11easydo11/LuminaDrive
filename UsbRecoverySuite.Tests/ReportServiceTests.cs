using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Reporting;
using Xunit;

namespace UsbRecoverySuite.Tests;

public class ReportServiceTests
{
    private readonly IReportService _reportService = new ReportService();

    [Fact]
    public void GenerateReports_Produce_Valid_Outputs()
    {
        var device = new UsbDeviceInfo
        {
            DiskIndex = 2,
            Model = "SanDisk Ultra",
            Manufacturer = "SanDisk",
            SerialNumber = "4C530001",
            Vid = "0781",
            Pid = "5581",
            BusType = "USB",
            UsbVersionSpeed = "USB 3.0 / SuperSpeed (5 Gbps)",
            DeviceType = UsbStorageDeviceType.UsbFlashDrive,
            ReportedCapacityBytes = 16L * 1024 * 1024 * 1024,
            PartitionStyle = PartitionTableType.MBR,
            Partitions = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    DiskIndex = 2,
                    PartitionNumber = 1,
                    SizeBytes = 16L * 1024 * 1024 * 1024 - 1048576,
                    StartingOffset = 1048576,
                    DriveLetters = new List<string> { "F:" }
                }
            }
        };

        var diags = new List<DiagnosticResult>
        {
            new DiagnosticResult
            {
                Category = DiagnosticCategory.Partition,
                Severity = HealthStatus.Ok,
                Title = "Partition Table Valid",
                Description = "MBR table is well formed.",
                RecommendedAction = "No action needed."
            }
        };

        var cap = new CapacityTestResult
        {
            ReportedCapacityBytes = 16L * 1024 * 1024 * 1024,
            AddressableCapacityBytes = 16L * 1024 * 1024 * 1024,
            VerifiedCapacityBytes = 16L * 1024 * 1024 * 1024,
            ReliableCapacityBytes = 16L * 1024 * 1024 * 1024,
            AverageReadSpeedMBps = 95.5,
            AverageWriteSpeedMBps = 32.1,
            Summary = "Test passed with 0 errors."
        };

        var repair = new RepairOperationResult
        {
            Success = true,
            ActionType = RepairActionType.CleanAndPartitionAll,
            OperationName = "Clean and Rebuild USB Storage",
            TargetDiskIndex = 2,
            Verified = true,
            VerificationSummary = "VERIFIED: 100% capacity allocated."
        };

        // Act
        string text = _reportService.GenerateTextReport(device, diags, cap, repair);
        string json = _reportService.GenerateJsonReport(device, diags, cap, repair);
        string html = _reportService.GenerateHtmlReport(device, diags, cap, repair);

        // Assert
        Assert.Contains("USB RECOVERY & DIAGNOSTIC SUITE REPORT", text);
        Assert.Contains("SanDisk Ultra", text);
        Assert.Contains("VID_0781", text);
        Assert.Contains("VERIFIED: 100% capacity allocated.", text);

        Assert.Contains("\"SanDisk Ultra\"", json);
        Assert.Contains("\"Vid\": \"0781\"", json);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("SanDisk Ultra", html);
        Assert.Contains("badge-ok", html);
    }
}
