using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Diagnostics;

public interface IDiagnosticEngine
{
    Task<List<DiagnosticResult>> RunFullDiagnosticsAsync(UsbDeviceInfo device);
    HealthStatus DetermineOverallHealth(IEnumerable<DiagnosticResult> results);
    string GetRecommendedActionSummary(IEnumerable<DiagnosticResult> results);
}
