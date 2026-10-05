namespace UsbRecoverySuite.Core.Models;

public class RepairOperationResult
{
    public bool Success { get; set; }
    public RepairActionType ActionType { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public int TargetDiskIndex { get; set; }
    public string TargetDiskModel { get; set; } = string.Empty;
    public UsbDeviceInfo? BeforeSnapshot { get; set; }
    public UsbDeviceInfo? AfterSnapshot { get; set; }
    public bool Verified { get; set; }
    public string VerificationSummary { get; set; } = string.Empty;
    public List<string> StepsExecuted { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class OperationHistoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public int DiskIndex { get; set; }
    public string DiskModel { get; set; } = string.Empty;
    public string DiskSerial { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Details { get; set; } = string.Empty;

    public string FormattedTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string StatusBadge => Success ? "SUCCESS" : "FAILED";
}

public class AppLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");
    public string FormattedLine => $"[{FormattedTime}] [{Level.ToString().ToUpper()}] [{Source}] {Message}";
}
