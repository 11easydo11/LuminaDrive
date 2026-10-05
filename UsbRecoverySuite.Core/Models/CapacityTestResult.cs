namespace UsbRecoverySuite.Core.Models;

public class CapacityTestResult
{
    public long ReportedCapacityBytes { get; set; }
    public long AddressableCapacityBytes { get; set; }
    public long VerifiedCapacityBytes { get; set; }
    public long ReliableCapacityBytes { get; set; }

    public long TestedBytes { get; set; }
    public int ReadErrorsCount { get; set; }
    public int WriteErrorsCount { get; set; }
    public int TotalErrorsCount => ReadErrorsCount + WriteErrorsCount;

    public double AverageReadSpeedMBps { get; set; }
    public double AverageWriteSpeedMBps { get; set; }
    public TimeSpan Duration { get; set; }

    public bool IsCompleted { get; set; }
    public bool IsCancelled { get; set; }

    public bool FakeDriveSuspected { get; set; }
    public int FakeDriveConfidencePercent { get; set; }
    public string FakeDriveEvidence { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;
    public List<string> DetailedFindings { get; set; } = new();

    public string ReportedFormatted => VolumeInfo.FormatBytes(ReportedCapacityBytes);
    public string AddressableFormatted => VolumeInfo.FormatBytes(AddressableCapacityBytes);
    public string VerifiedFormatted => VolumeInfo.FormatBytes(VerifiedCapacityBytes);
    public string ReliableFormatted => VolumeInfo.FormatBytes(ReliableCapacityBytes);

    public const string CapacityWarningNotice = 
        "Notice: Formatting does not increase the physical capacity of the drive. " +
        "It only structures addressable space according to filesystem rules.";
}

public class CapacityProgressReport
{
    public double Percentage { get; set; }
    public string CurrentPhase { get; set; } = string.Empty;
    public long BytesProcessed { get; set; }
    public long TotalBytesToProcess { get; set; }
    public double SpeedMBps { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan RemainingTime { get; set; }
    public int ErrorsFound { get; set; }
}
