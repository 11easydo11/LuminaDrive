using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Reporting;

public interface IReportService
{
    string GenerateTextReport(
        UsbDeviceInfo device,
        IEnumerable<DiagnosticResult> diagnostics,
        CapacityTestResult? capacityTest = null,
        RepairOperationResult? repairResult = null);

    string GenerateJsonReport(
        UsbDeviceInfo device,
        IEnumerable<DiagnosticResult> diagnostics,
        CapacityTestResult? capacityTest = null,
        RepairOperationResult? repairResult = null);

    string GenerateHtmlReport(
        UsbDeviceInfo device,
        IEnumerable<DiagnosticResult> diagnostics,
        CapacityTestResult? capacityTest = null,
        RepairOperationResult? repairResult = null);

    Task ExportReportAsync(string filePath, string content);
}
