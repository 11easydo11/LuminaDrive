using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Logging;

namespace UsbRecoverySuite.Core.Services.Storage;

public class WindowsStorageService : IWindowsStorageService
{
    private readonly IAppLogger _logger;

    public WindowsStorageService(IAppLogger logger)
    {
        _logger = logger;
    }

    public Task<List<UsbDeviceInfo>> GetAllDisksAsync()
    {
        return Task.Run(() =>
        {
            var diskList = new List<UsbDeviceInfo>();

            try
            {
                // Query root\Microsoft\Windows\Storage for MSFT_Disk
                var msftDisks = QueryMsftDisks();
                var msftPartitions = QueryMsftPartitions();
                var msftVolumes = QueryMsftVolumes();
                var win32Drives = QueryWin32DiskDrives();
                var usbPnpEntities = QueryUsbPnpEntities();

                // If MSFT_Disk returned items, use it as primary source
                if (msftDisks.Count > 0)
                {
                    foreach (var disk in msftDisks)
                    {
                        int diskNum = disk.DiskIndex;

                        // Match win32 info
                        if (win32Drives.TryGetValue(diskNum, out var w32))
                        {
                            if (disk.Model == "Not available" && !string.IsNullOrWhiteSpace(w32.Model))
                                disk.Model = w32.Model;
                            if (disk.Manufacturer == "Not available" && !string.IsNullOrWhiteSpace(w32.Manufacturer))
                                disk.Manufacturer = w32.Manufacturer;
                            if (disk.PnpDeviceId == "Not available" && !string.IsNullOrWhiteSpace(w32.PnpDeviceId))
                                disk.PnpDeviceId = w32.PnpDeviceId;
                            if (disk.FirmwareRevision == "Not available" && !string.IsNullOrWhiteSpace(w32.FirmwareRevision))
                                disk.FirmwareRevision = w32.FirmwareRevision;
                            if (disk.SerialNumber == "Not available" && !string.IsNullOrWhiteSpace(w32.SerialNumber))
                                disk.SerialNumber = CleanSerial(w32.SerialNumber);
                            if (disk.ReportedCapacityBytes == 0 && w32.ReportedCapacityBytes > 0)
                                disk.ReportedCapacityBytes = w32.ReportedCapacityBytes;
                            if (w32.IsRemovable)
                                disk.IsRemovable = true;
                        }

                        // Attach partitions
                        if (msftPartitions.TryGetValue(diskNum, out var parts))
                        {
                            disk.Partitions = parts;
                        }

                        // Attach volumes for partitions
                        var volumesForDisk = new List<VolumeInfo>();
                        foreach (var part in disk.Partitions)
                        {
                            foreach (var letter in part.DriveLetters)
                            {
                                if (msftVolumes.TryGetValue(letter.ToUpperInvariant(), out var vol))
                                {
                                    part.Volume = vol;
                                    if (!volumesForDisk.Any(v => v.DriveLetter.Equals(vol.DriveLetter, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        volumesForDisk.Add(vol);
                                    }
                                }
                            }
                        }

                        disk.Volumes = volumesForDisk;

                        // Extract VID & PID and USB Speed
                        ResolveUsbDetails(disk, usbPnpEntities);

                        // Determine device type
                        ClassifyDeviceType(disk);

                        diskList.Add(disk);
                    }
                }
                else
                {
                    // Fallback to Win32_DiskDrive only
                    foreach (var kvp in win32Drives)
                    {
                        var disk = kvp.Value;
                        ResolveUsbDetails(disk, usbPnpEntities);
                        ClassifyDeviceType(disk);
                        diskList.Add(disk);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(nameof(WindowsStorageService), $"Failed to retrieve disks: {ex.Message}");
            }

            return diskList.OrderBy(d => d.DiskIndex).ToList();
        });
    }

    public async Task<UsbDeviceInfo?> GetDiskByIndexAsync(int diskIndex)
    {
        var disks = await GetAllDisksAsync();
        return disks.FirstOrDefault(d => d.DiskIndex == diskIndex);
    }

    public Task<bool> RescanStorageBusAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                _logger.LogInfo(nameof(WindowsStorageService), "Rescanning storage bus via diskpart rescan...");
                var psi = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = "",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.StandardInput.WriteLine("rescan");
                    proc.StandardInput.WriteLine("exit");
                    proc.StandardInput.Flush();
                    if (!proc.WaitForExit(15000))
                    {
                        proc.Kill(entireProcessTree: true);
                        _logger.LogWarning(nameof(WindowsStorageService), "DiskPart rescan timed out.");
                        return false;
                    }
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(nameof(WindowsStorageService), $"Diskpart rescan failed: {ex.Message}");
            }
            return false;
        });
    }

    #region Private WMI and Storage API Methods

    private List<UsbDeviceInfo> QueryMsftDisks()
    {
        var list = new List<UsbDeviceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Disk");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    int index = Convert.ToInt32(obj["Number"] ?? -1);
                    if (index < 0) continue;

                    var info = new UsbDeviceInfo
                    {
                        DiskIndex = index,
                        DeviceId = $@"\\.\PhysicalDrive{index}",
                        Model = obj["FriendlyName"]?.ToString()?.Trim() ?? "Not available",
                        SerialNumber = CleanSerial(obj["SerialNumber"]?.ToString()),
                        ReportedCapacityBytes = Convert.ToInt64(obj["Size"] ?? 0L),
                        LogicalSectorSize = Convert.ToInt32(obj["LogicalSectorSize"] ?? 512),
                        PhysicalSectorSize = Convert.ToInt32(obj["PhysicalSectorSize"] ?? 512),
                        IsSystem = Convert.ToBoolean(obj["IsSystem"] ?? false),
                        IsBoot = Convert.ToBoolean(obj["IsBoot"] ?? false),
                        IsReadOnly = Convert.ToBoolean(obj["IsReadOnly"] ?? false),
                        IsOffline = Convert.ToBoolean(obj["IsOffline"] ?? false)
                    };

                    // Bus Type
                    int busCode = Convert.ToInt32(obj["BusType"] ?? 0);
                    info.BusType = MapBusType(busCode);

                    // Partition Style: 0=RAW, 1=MBR, 2=GPT
                    int pStyle = Convert.ToInt32(obj["PartitionStyle"] ?? 0);
                    info.PartitionStyle = pStyle switch
                    {
                        1 => PartitionTableType.MBR,
                        2 => PartitionTableType.GPT,
                        _ => PartitionTableType.RAW
                    };

                    // Health
                    int health = Convert.ToInt32(obj["HealthStatus"] ?? 0);
                    info.Health = health switch
                    {
                        0 => HealthStatus.Ok,
                        1 => HealthStatus.Warning,
                        2 => HealthStatus.Critical,
                        _ => HealthStatus.Unknown
                    };

                    list.Add(info);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(WindowsStorageService), $"QueryMsftDisks note: {ex.Message}");
        }
        return list;
    }

    private Dictionary<int, List<PartitionInfo>> QueryMsftPartitions()
    {
        var dict = new Dictionary<int, List<PartitionInfo>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Partition");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    int diskNum = Convert.ToInt32(obj["DiskNumber"] ?? -1);
                    if (diskNum < 0) continue;

                    int partNum = Convert.ToInt32(obj["PartitionNumber"] ?? 0);
                    long offset = Convert.ToInt64(obj["Offset"] ?? 0L);
                    long size = Convert.ToInt64(obj["Size"] ?? 0L);
                    string? gptGuid = obj["GptType"]?.ToString()?.Trim('{', '}', ' ');
                    bool isActive = Convert.ToBoolean(obj["IsActive"] ?? false);
                    bool isBoot = Convert.ToBoolean(obj["IsBoot"] ?? false);

                    var part = new PartitionInfo
                    {
                        DiskIndex = diskNum,
                        PartitionNumber = partNum,
                        StartingOffset = offset,
                        SizeBytes = size,
                        GptTypeGuid = gptGuid,
                        IsActive = isActive,
                        IsBootable = isBoot,
                        PartitionType = ResolvePartitionType(gptGuid)
                    };

                    // Drive Letter
                    string? letter = obj["DriveLetter"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(letter) && letter != "\0" && char.IsLetter(letter[0]))
                    {
                        part.DriveLetters.Add($"{letter[0]}:");
                    }

                    if (!dict.TryGetValue(diskNum, out var list))
                    {
                        list = new List<PartitionInfo>();
                        dict[diskNum] = list;
                    }
                    list.Add(part);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(WindowsStorageService), $"QueryMsftPartitions note: {ex.Message}");
        }
        return dict;
    }

    private Dictionary<string, VolumeInfo> QueryMsftVolumes()
    {
        var dict = new Dictionary<string, VolumeInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Volume");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    string? letterChar = obj["DriveLetter"]?.ToString()?.Trim();
                    string letter = !string.IsNullOrEmpty(letterChar) && letterChar != "\0" && char.IsLetter(letterChar[0])
                        ? $"{letterChar[0]}:"
                        : string.Empty;

                    long size = Convert.ToInt64(obj["Size"] ?? 0L);
                    long remaining = Convert.ToInt64(obj["SizeRemaining"] ?? 0L);
                    string fs = obj["FileSystem"]?.ToString()?.Trim() ?? "RAW";
                    string label = obj["FileSystemLabel"]?.ToString()?.Trim() ?? string.Empty;
                    int healthCode = Convert.ToInt32(obj["HealthStatus"] ?? 0);

                    var vol = new VolumeInfo
                    {
                        DriveLetter = letter,
                        VolumeLabel = label,
                        FileSystem = string.IsNullOrWhiteSpace(fs) ? "RAW" : fs,
                        TotalBytes = size,
                        FreeBytes = remaining,
                        Health = healthCode switch
                        {
                            0 => HealthStatus.Ok,
                            1 => HealthStatus.Warning,
                            2 => HealthStatus.Critical,
                            _ => HealthStatus.Unknown
                        }
                    };

                    if (!string.IsNullOrEmpty(letter))
                    {
                        dict[letter] = vol;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(WindowsStorageService), $"QueryMsftVolumes note: {ex.Message}");
        }

        // Also query Win32_LogicalDisk to supplement drive labels/free space
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 2 OR DriveType = 3");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    string? devId = obj["DeviceID"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(devId)) continue;

                    long size = Convert.ToInt64(obj["Size"] ?? 0L);
                    long free = Convert.ToInt64(obj["FreeSpace"] ?? 0L);
                    string fs = obj["FileSystem"]?.ToString()?.Trim() ?? "Unknown";
                    string label = obj["VolumeName"]?.ToString()?.Trim() ?? string.Empty;

                    if (!dict.TryGetValue(devId, out var existing))
                    {
                        dict[devId] = new VolumeInfo
                        {
                            DriveLetter = devId,
                            VolumeLabel = label,
                            FileSystem = fs,
                            TotalBytes = size,
                            FreeBytes = free,
                            Health = HealthStatus.Ok
                        };
                    }
                    else
                    {
                        if (existing.TotalBytes == 0 && size > 0) existing.TotalBytes = size;
                        if (existing.FreeBytes == 0 && free > 0) existing.FreeBytes = free;
                        if (string.IsNullOrWhiteSpace(existing.VolumeLabel) && !string.IsNullOrWhiteSpace(label)) existing.VolumeLabel = label;
                        if (existing.FileSystem == "RAW" && !string.IsNullOrWhiteSpace(fs) && fs != "Unknown") existing.FileSystem = fs;
                    }
                }
            }
        }
        catch
        {
            // Ignore supplementary logical disk failures
        }

        return dict;
    }

    private Dictionary<int, UsbDeviceInfo> QueryWin32DiskDrives()
    {
        var dict = new Dictionary<int, UsbDeviceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Index, Model, Manufacturer, PNPDeviceID, DeviceID, Size, MediaType, InterfaceType, FirmwareRevision, BytesPerSector, Status FROM Win32_DiskDrive");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    int index = Convert.ToInt32(obj["Index"] ?? -1);
                    if (index < 0) continue;

                    string mediaType = obj["MediaType"]?.ToString() ?? string.Empty;
                    bool isRemovable = mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);

                    var device = new UsbDeviceInfo
                    {
                        DiskIndex = index,
                        DeviceId = obj["DeviceID"]?.ToString() ?? $@"\\.\PhysicalDrive{index}",
                        Model = obj["Model"]?.ToString()?.Trim() ?? "Not available",
                        Manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "Not available",
                        PnpDeviceId = obj["PNPDeviceID"]?.ToString()?.Trim() ?? "Not available",
                        FirmwareRevision = obj["FirmwareRevision"]?.ToString()?.Trim() ?? "Not available",
                        ReportedCapacityBytes = Convert.ToInt64(obj["Size"] ?? 0L),
                        LogicalSectorSize = Convert.ToInt32(obj["BytesPerSector"] ?? 512),
                        IsRemovable = isRemovable,
                        SmartStatus = obj["Status"]?.ToString()?.Trim() ?? "OK"
                    };

                    string iface = obj["InterfaceType"]?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(iface))
                    {
                        device.BusType = iface;
                    }

                    dict[index] = device;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(WindowsStorageService), $"QueryWin32DiskDrives note: {ex.Message}");
        }
        return dict;
    }

    private List<(string DeviceId, string Name, string HardwareId)> QueryUsbPnpEntities()
    {
        var list = new List<(string DeviceId, string Name, string HardwareId)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT DeviceID, Name, HardwareID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%' OR DeviceID LIKE 'STORAGE%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    string id = obj["DeviceID"]?.ToString() ?? string.Empty;
                    string name = obj["Name"]?.ToString() ?? string.Empty;
                    string hw = string.Empty;
                    if (obj["HardwareID"] is string[] hwArray && hwArray.Length > 0)
                    {
                        hw = string.Join(";", hwArray);
                    }
                    list.Add((id, name, hw));
                }
            }
        }
        catch
        {
            // Non-critical
        }
        return list;
    }

    private void ResolveUsbDetails(UsbDeviceInfo disk, List<(string DeviceId, string Name, string HardwareId)> pnpEntities)
    {
        // Try extracting VID & PID from PnpDeviceId
        var match = Regex.Match(disk.PnpDeviceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            disk.Vid = match.Groups[1].Value.ToUpperInvariant();
            disk.Pid = match.Groups[2].Value.ToUpperInvariant();
        }
        else
        {
            // Search in USB PNP entities for matching drive or storage controller
            foreach (var entity in pnpEntities)
            {
                var m = Regex.Match(entity.DeviceId + " " + entity.HardwareId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    // Check if entity name or ID matches model or vendor
                    if (disk.Model != "Not available" && entity.Name.Contains(disk.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        disk.Vid = m.Groups[1].Value.ToUpperInvariant();
                        disk.Pid = m.Groups[2].Value.ToUpperInvariant();
                        break;
                    }
                }
            }
        }

        // Try detecting USB speed
        if (disk.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) || disk.BusType.Equals("UASP", StringComparison.OrdinalIgnoreCase))
        {
            // Look up USB speed indicators in PNP entities or hardware IDs
            bool isUsb3 = pnpEntities.Any(p => (p.HardwareId.Contains("USB 3.0", StringComparison.OrdinalIgnoreCase) ||
                                               p.HardwareId.Contains("USB3", StringComparison.OrdinalIgnoreCase) ||
                                               p.Name.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase) ||
                                               p.DeviceId.Contains("USB\\ROOT_HUB30", StringComparison.OrdinalIgnoreCase)));

            if (disk.BusType.Equals("UASP", StringComparison.OrdinalIgnoreCase))
            {
                disk.UsbVersionSpeed = "USB 3.2 Gen 1 (UASP / SuperSpeed 5+ Gbps)";
            }
            else if (isUsb3)
            {
                disk.UsbVersionSpeed = "USB 3.0 / SuperSpeed (5 Gbps)";
            }
            else
            {
                disk.UsbVersionSpeed = "USB 2.0 High-Speed (480 Mbps) or 3.x";
            }
        }
        else
        {
            disk.UsbVersionSpeed = "Not applicable (Non-USB bus)";
        }
    }

    private void ClassifyDeviceType(UsbDeviceInfo disk)
    {
        var bus = (disk.BusType ?? string.Empty).Trim().ToUpperInvariant();

        if (bus != "USB" && bus != "UASP" && !disk.IsRemovable)
        {
            disk.DeviceType = UsbStorageDeviceType.InternalDisk;
            return;
        }

        if (disk.IsRemovable)
        {
            disk.DeviceType = UsbStorageDeviceType.UsbFlashDrive;
            return;
        }

        // Fixed disk over USB
        if (bus == "USB" || bus == "UASP")
        {
            // If capacity >= 64GB and not removable, typically external SSD/HDD
            if (disk.ReportedCapacityBytes >= 64L * 1024 * 1024 * 1024)
            {
                disk.DeviceType = UsbStorageDeviceType.ExternalUsbDisk;
            }
            else
            {
                disk.DeviceType = UsbStorageDeviceType.UsbFlashDrive;
            }
            return;
        }

        disk.DeviceType = UsbStorageDeviceType.OtherUsbStorage;
    }

    private string MapBusType(int busType)
    {
        return busType switch
        {
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            4 => "1394",
            5 => "SSA",
            6 => "Fibre Channel",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual",
            15 => "FileBackedVirtual",
            16 => "StorageSpaces",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            _ => "Not available"
        };
    }

    private string ResolvePartitionType(string? gptGuid)
    {
        if (string.IsNullOrWhiteSpace(gptGuid)) return "Basic data partition";
        var clean = gptGuid.ToLowerInvariant();
        return clean switch
        {
            "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic data partition (Microsoft)",
            "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI System Partition (ESP)",
            "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Windows Recovery Partition",
            "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Microsoft Reserved (MSR)",
            "af9b60a0-1431-4f62-bc68-3311714a69ad" => "Windows LDM Data",
            "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3" => "Windows LDM Metadata",
            _ => $"GUID {{{clean}}}"
        };
    }

    private string CleanSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Not available";
        var cleaned = raw.Trim().TrimEnd('&', '0', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? "Not available" : cleaned;
    }

    #endregion
}
