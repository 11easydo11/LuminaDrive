namespace UsbRecoverySuite.Core.Models;

public class VolumeInfo
{
    public string DriveLetter { get; set; } = string.Empty;
    public string VolumeGuid { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = "Unknown";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0.0;

    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsReadOnly { get; set; }
    public HealthStatus Health { get; set; } = HealthStatus.Unknown;
    public List<string> MountPoints { get; set; } = new();

    public string DisplayLabel => string.IsNullOrWhiteSpace(VolumeLabel) 
        ? (string.IsNullOrWhiteSpace(DriveLetter) ? "Volume" : $"Local Disk ({DriveLetter})")
        : $"{VolumeLabel} ({DriveLetter})";

    public string TotalFormatted => FormatBytes(TotalBytes);
    public string FreeFormatted => FormatBytes(FreeBytes);
    public string UsedFormatted => FormatBytes(UsedBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n2} {suffixes[counter]}";
    }
}
