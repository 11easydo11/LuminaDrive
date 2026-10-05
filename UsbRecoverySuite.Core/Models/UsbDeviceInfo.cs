namespace UsbRecoverySuite.Core.Models;

public class UsbDeviceInfo
{
    public int DiskIndex { get; set; }
    public string DeviceId { get; set; } = string.Empty; // e.g. \\.\PhysicalDrive1
    public string Model { get; set; } = "Not available";
    public string Manufacturer { get; set; } = "Not available";
    public string SerialNumber { get; set; } = "Not available";
    public string PnpDeviceId { get; set; } = "Not available";
    public string Vid { get; set; } = "Not available";
    public string Pid { get; set; } = "Not available";
    public string UsbVersionSpeed { get; set; } = "Not available";
    public string FirmwareRevision { get; set; } = "Not available";
    public string BusType { get; set; } = "Not available";
    public UsbStorageDeviceType DeviceType { get; set; } = UsbStorageDeviceType.UsbFlashDrive;

    public bool IsRemovable { get; set; }
    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsPageFile { get; set; }

    public long ReportedCapacityBytes { get; set; }
    public int LogicalSectorSize { get; set; } = 512;
    public int PhysicalSectorSize { get; set; } = 512;

    public PartitionTableType PartitionStyle { get; set; } = PartitionTableType.Unknown;
    public bool IsReadOnly { get; set; }
    public bool IsOffline { get; set; }

    public List<PartitionInfo> Partitions { get; set; } = new();
    public List<VolumeInfo> Volumes { get; set; } = new();

    public long TotalPartitionedBytes => Partitions.Sum(p => p.SizeBytes);
    public long UnallocatedBytes => Math.Max(0, ReportedCapacityBytes - TotalPartitionedBytes);
    public double UnallocatedPercent => ReportedCapacityBytes > 0 
        ? Math.Max(0, (double)UnallocatedBytes / ReportedCapacityBytes * 100.0) 
        : 0.0;

    public HealthStatus Health { get; set; } = HealthStatus.Unknown;
    public string SmartStatus { get; set; } = "Not available";
    public string SystemConstraints { get; set; } = "None";
    public string EventLogSummary { get; set; } = "No recent errors found";
    public List<string> RecentDeviceEvents { get; set; } = new();

    // UI helpers
    public string DisplayName => string.IsNullOrWhiteSpace(Model) || Model == "Not available"
        ? $"Disk {DiskIndex}"
        : $"{Model} (Disk {DiskIndex})";

    public string ReportedCapacityFormatted => VolumeInfo.FormatBytes(ReportedCapacityBytes);
    public string TotalPartitionedFormatted => VolumeInfo.FormatBytes(TotalPartitionedBytes);
    public string UnallocatedFormatted => VolumeInfo.FormatBytes(UnallocatedBytes);

    public string DriveLettersDisplay
    {
        get
        {
            var letters = Volumes.Select(v => v.DriveLetter).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
            if (letters.Count == 0)
            {
                letters = Partitions.SelectMany(p => p.DriveLetters).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
            }
            return letters.Count > 0 ? string.Join(", ", letters) : "No drive letters";
        }
    }

    public string PrimaryFileSystem
    {
        get
        {
            var fsList = Volumes.Select(v => v.FileSystem).Where(f => !string.IsNullOrWhiteSpace(f) && f != "Unknown").Distinct().ToList();
            return fsList.Count > 0 ? string.Join(", ", fsList) : (Partitions.Count > 0 ? "RAW / Unformatted" : "Uninitialized");
        }
    }

    public string DeviceTypeDisplay => DeviceType switch
    {
        UsbStorageDeviceType.UsbFlashDrive => "USB Flash Drive",
        UsbStorageDeviceType.ExternalUsbDisk => "External USB Disk (SSD/HDD)",
        UsbStorageDeviceType.OtherUsbStorage => "Other USB Storage Device",
        UsbStorageDeviceType.InternalDisk => "Internal System/Data Disk",
        _ => "Unknown Storage"
    };

    public bool IsSafeToFormat => !IsSystem && !IsBoot && !IsPageFile && DeviceType != UsbStorageDeviceType.InternalDisk;

    public UsbDeviceInfo Clone()
    {
        var copy = (UsbDeviceInfo)this.MemberwiseClone();
        copy.Partitions = Partitions.Select(p => new PartitionInfo
        {
            DiskIndex = p.DiskIndex,
            PartitionNumber = p.PartitionNumber,
            StartingOffset = p.StartingOffset,
            SizeBytes = p.SizeBytes,
            PartitionType = p.PartitionType,
            GptTypeGuid = p.GptTypeGuid,
            IsBootable = p.IsBootable,
            IsActive = p.IsActive,
            DriveLetters = new List<string>(p.DriveLetters),
            Volume = p.Volume != null ? new VolumeInfo
            {
                DriveLetter = p.Volume.DriveLetter,
                VolumeGuid = p.Volume.VolumeGuid,
                VolumeLabel = p.Volume.VolumeLabel,
                FileSystem = p.Volume.FileSystem,
                TotalBytes = p.Volume.TotalBytes,
                FreeBytes = p.Volume.FreeBytes,
                IsSystem = p.Volume.IsSystem,
                IsBoot = p.Volume.IsBoot,
                IsReadOnly = p.Volume.IsReadOnly,
                Health = p.Volume.Health,
                MountPoints = new List<string>(p.Volume.MountPoints)
            } : null
        }).ToList();

        copy.Volumes = Volumes.Select(v => new VolumeInfo
        {
            DriveLetter = v.DriveLetter,
            VolumeGuid = v.VolumeGuid,
            VolumeLabel = v.VolumeLabel,
            FileSystem = v.FileSystem,
            TotalBytes = v.TotalBytes,
            FreeBytes = v.FreeBytes,
            IsSystem = v.IsSystem,
            IsBoot = v.IsBoot,
            IsReadOnly = v.IsReadOnly,
            Health = v.Health,
            MountPoints = new List<string>(v.MountPoints)
        }).ToList();

        copy.RecentDeviceEvents = new List<string>(RecentDeviceEvents);
        return copy;
    }
}
