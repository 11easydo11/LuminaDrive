namespace UsbRecoverySuite.Core.Models;

public class PartitionInfo
{
    public int DiskIndex { get; set; }
    public int PartitionNumber { get; set; }
    public long StartingOffset { get; set; }
    public long SizeBytes { get; set; }
    public string PartitionType { get; set; } = "Basic data partition";
    public string? GptTypeGuid { get; set; }
    public bool IsBootable { get; set; }
    public bool IsActive { get; set; }
    public List<string> DriveLetters { get; set; } = new();
    public VolumeInfo? Volume { get; set; }

    public string SizeFormatted => VolumeInfo.FormatBytes(SizeBytes);
    public string OffsetFormatted => VolumeInfo.FormatBytes(StartingOffset);
    public string DriveLettersDisplay => DriveLetters.Count > 0 ? string.Join(", ", DriveLetters) : "None";
}
