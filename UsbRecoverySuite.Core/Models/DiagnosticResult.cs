namespace UsbRecoverySuite.Core.Models;

public class DiagnosticResult
{
    public DiagnosticCategory Category { get; set; }
    public HealthStatus Severity { get; set; } = HealthStatus.Ok;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;
    public bool CanAutoFix { get; set; }
    public RepairActionType FixType { get; set; } = RepairActionType.None;

    public string SeverityBadge => Severity switch
    {
        HealthStatus.Ok => "OK",
        HealthStatus.Warning => "WARNING",
        HealthStatus.Error => "ERROR",
        HealthStatus.Critical => "CRITICAL",
        _ => "UNKNOWN"
    };
}
