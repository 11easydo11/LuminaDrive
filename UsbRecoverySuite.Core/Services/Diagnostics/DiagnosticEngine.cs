using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Localization;
using UsbRecoverySuite.Core.Services.Logging;

namespace UsbRecoverySuite.Core.Services.Diagnostics;

public class DiagnosticEngine : IDiagnosticEngine
{
    private readonly IWindowsEventLogService _eventLogService;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService? _loc;

    public DiagnosticEngine(IWindowsEventLogService eventLogService, IAppLogger logger, ILocalizationService? loc = null)
    {
        _eventLogService = eventLogService;
        _logger = logger;
        _loc = loc;
    }

    private bool IsRu => (_loc?.CurrentLanguage ?? "ru") == "ru";

    public async Task<List<DiagnosticResult>> RunFullDiagnosticsAsync(UsbDeviceInfo device)
    {
        var results = new List<DiagnosticResult>();

        if (device == null)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Safety,
                Severity = HealthStatus.Error,
                Title = IsRu ? "Накопитель не выбран" : "Device Not Found",
                Description = IsRu ? "Выберите USB-накопитель в списке вверху или подключите его к компьютеру." : "No device is currently selected or the device has been disconnected.",
                RecommendedAction = IsRu ? "Вставьте флешку в USB-порт и выберите её в выпадающем списке." : "Select a valid USB drive or re-connect the device."
            });
            return results;
        }

        _logger.LogInfo(nameof(DiagnosticEngine), $"Starting diagnostic analysis for Disk {device.DiskIndex} ({device.Model})...");

        // 1. Safety & System Disks
        CheckSafetyAndSystemStatus(device, results);

        // 2. Partition Structure Diagnostics
        CheckPartitionStructure(device, results);

        // 3. Filesystem Diagnostics
        CheckFilesystemStatus(device, results);

        // 4. Device and Hardware Status (Read-only, offline, SMART)
        CheckDeviceHardwareStatus(device, results);

        // 5. Windows Event Log Diagnostics
        await CheckEventLogStatusAsync(device, results);

        // 6. Capacity sanity checks
        CheckCapacityDiagnostics(device, results);

        _logger.LogInfo(nameof(DiagnosticEngine), 
            $"Diagnostics completed for Disk {device.DiskIndex}. Total findings: {results.Count} (Errors/Warnings: {results.Count(r => r.Severity >= HealthStatus.Warning)})");

        return results;
    }

    public HealthStatus DetermineOverallHealth(IEnumerable<DiagnosticResult> results)
    {
        if (results.Any(r => r.Severity == HealthStatus.Critical)) return HealthStatus.Critical;
        if (results.Any(r => r.Severity == HealthStatus.Error)) return HealthStatus.Error;
        if (results.Any(r => r.Severity == HealthStatus.Warning)) return HealthStatus.Warning;
        return HealthStatus.Ok;
    }

    public string GetRecommendedActionSummary(IEnumerable<DiagnosticResult> results)
    {
        var critical = results.FirstOrDefault(r => r.Severity == HealthStatus.Critical);
        if (critical != null) return critical.RecommendedAction;

        var error = results.FirstOrDefault(r => r.Severity == HealthStatus.Error);
        if (error != null) return error.RecommendedAction;

        var warning = results.FirstOrDefault(r => r.Severity == HealthStatus.Warning);
        if (warning != null) return warning.RecommendedAction;

        return IsRu 
            ? "Флешка исправна, весь объем доступен. Дополнительных действий не требуется." 
            : "Drive is healthy and functioning optimally. No actions required.";
    }

    #region Diagnostic Checks

    private void CheckSafetyAndSystemStatus(UsbDeviceInfo device, List<DiagnosticResult> results)
    {
        if (device.IsSystem || device.IsBoot || device.IsPageFile)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Safety,
                Severity = HealthStatus.Critical,
                Title = IsRu ? "Обнаружен системный диск Windows" : "System / Boot Disk Detected",
                Description = IsRu ? "Этот диск содержит работающую операционную систему Windows (Boot/System/PageFile)." : "This drive is an essential part of the running Windows operating system (Boot/System/PageFile).",
                TechnicalDetails = $"IsSystem: {device.IsSystem}, IsBoot: {device.IsBoot}, IsPageFile: {device.IsPageFile}",
                RecommendedAction = IsRu ? "Форматирование заблокировано в целях защиты вашей системы." : "Do NOT perform any repair or formatting operations on this disk.",
                CanAutoFix = false
            });
        }

        if (device.DeviceType == UsbStorageDeviceType.InternalDisk)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Safety,
                Severity = HealthStatus.Warning,
                Title = IsRu ? "Внутренний накопитель (NVMe/SATA)" : "Internal Storage Device",
                Description = IsRu ? "Этот диск подключен через внутреннюю шину материнской платы, а не через USB." : "This drive is connected via an internal storage bus (NVMe/SATA/SCSI).",
                TechnicalDetails = $"BusType: {device.BusType}, Removable: {device.IsRemovable}",
                RecommendedAction = IsRu ? "Защита активна: внутренние диски защищены от случайной перезаписи." : "USB Suite protections are active to prevent accidental manipulation of internal drives.",
                CanAutoFix = false
            });
        }
        else if (device.DeviceType == UsbStorageDeviceType.ExternalUsbDisk)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Safety,
                Severity = HealthStatus.Warning,
                Title = IsRu ? "Внешний USB SSD / Жесткий диск" : "External USB SSD / HDD Detected",
                Description = IsRu ? "Это внешний емкий жесткий диск или SSD, а не обычная USB-флешка." : "This device is a large external SSD or HDD rather than a simple flash drive.",
                TechnicalDetails = $"Reported Capacity: {device.ReportedCapacityFormatted}, Removable: {device.IsRemovable}",
                RecommendedAction = IsRu ? "Убедитесь в наличии резервной копии перед форматированием." : "Verify all partitions and backups before executing any destructive operations.",
                CanAutoFix = false
            });
        }
    }

    private void CheckPartitionStructure(UsbDeviceInfo device, List<DiagnosticResult> results)
    {
        // Check for RAW / unpartitioned disk
        if (device.PartitionStyle == PartitionTableType.RAW || device.Partitions.Count == 0)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Partition,
                Severity = HealthStatus.Error,
                Title = IsRu ? "Диск не инициализирован или разметка повреждена" : "Uninitialized or Corrupted Partition Table",
                Description = IsRu ? "Windows видит физическое устройство, но на нем нет рабочей таблицы разделов GPT или MBR." : "Windows detects the physical drive, but no valid GPT or MBR partition table exists.",
                TechnicalDetails = $"PartitionStyle: {device.PartitionStyle}, Partitions Count: {device.Partitions.Count}",
                RecommendedAction = IsRu
                    ? "Нажмите «Вернуть полный объем», чтобы создать новую разметку. Все файлы на диске будут удалены."
                    : "Choose “Reclaim Full Capacity” to create a new partition layout. All files on the drive will be erased.",
                CanAutoFix = true,
                FixType = RepairActionType.CleanAndPartitionAll
            });
            return;
        }

        // Check for Lost / Unallocated space (The classic "16GB flash shows 500MB" scenario!)
        long unallocated = device.UnallocatedBytes;
        double unallocatedPct = device.UnallocatedPercent;

        if (unallocated > 500 * 1024 * 1024L && unallocatedPct >= 8.0) // >500MB and >8%
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Partition,
                Severity = HealthStatus.Error,
                Title = IsRu ? "Обнаружена потеря памяти (нераспределенное место)" : "Substantial Unallocated Space Detected",
                Description = IsRu 
                    ? $"На флешке потеряно {device.UnallocatedFormatted} ({unallocatedPct:F1}%) памяти! Физически накопитель имеет {device.ReportedCapacityFormatted}, но Windows видит только {device.TotalPartitionedFormatted}."
                    : $"The drive has {device.UnallocatedFormatted} ({unallocatedPct:F1}%) of lost, unpartitioned space that Windows cannot use. Physical capacity is {device.ReportedCapacityFormatted}, but only {device.TotalPartitionedFormatted} is partitioned.",
                TechnicalDetails = $"Reported Capacity: {device.ReportedCapacityBytes} bytes, Allocated: {device.TotalPartitionedBytes} bytes, Lost: {unallocated} bytes",
                RecommendedAction = IsRu 
                    ? "Нажмите «Вернуть полный объем»: программа заменит разметку и создаст один раздел на доступный объём накопителя. Все файлы будут удалены."
                    : "Choose “Reclaim Full Capacity” to replace the partition layout with one partition using the available drive capacity. All files will be erased.",
                CanAutoFix = true,
                FixType = RepairActionType.CleanAndPartitionAll
            });
        }

        // Check for Multiple Unexpected Partitions on USB Flash Drive (e.g. leftover from Linux live USB / Rufus)
        if (device.DeviceType == UsbStorageDeviceType.UsbFlashDrive && device.Partitions.Count > 1)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Partition,
                Severity = HealthStatus.Warning,
                Title = IsRu ? "Несколько лишних разделов на флешке" : "Multiple Partitions on USB Flash Drive",
                Description = IsRu 
                    ? $"Флешка разбита на {device.Partitions.Count} разделов (часто бывает после записи загрузочных ISO-образов). Бытовые устройства и Проводник видят только первый."
                    : $"The USB drive has {device.Partitions.Count} partitions. This frequently occurs after writing Linux or installer ISO images, which confuses media players and standard Windows access.",
                TechnicalDetails = string.Join("; ", device.Partitions.Select(p => $"Part #{p.PartitionNumber}: {p.SizeFormatted} ({p.PartitionType})")),
                RecommendedAction = IsRu ? "Объедините разделы в один целый том exFAT." : "Rebuild into a single clean partition using exFAT or FAT32 for universal compatibility.",
                CanAutoFix = true,
                FixType = RepairActionType.CleanAndPartitionAll
            });
        }

        // Check for abnormally small primary partition
        if (device.ReportedCapacityBytes > 4L * 1024 * 1024 * 1024 && device.Partitions.Count == 1)
        {
            var p = device.Partitions[0];
            if (p.SizeBytes < 1024 * 1024 * 1024L) // Partition < 1GB on >4GB drive
            {
                results.Add(new DiagnosticResult
                {
                    Category = DiagnosticCategory.Partition,
                    Severity = HealthStatus.Error,
                    Title = IsRu ? "Основной раздел слишком мал" : "Primary Partition Abnormally Small",
                    Description = IsRu 
                        ? $"Рабочий раздел занимает всего {p.SizeFormatted}, хотя физически флешка вмещает {device.ReportedCapacityFormatted}."
                        : $"The only active partition is {p.SizeFormatted}, but the drive physically supports {device.ReportedCapacityFormatted}.",
                    TechnicalDetails = $"Partition 1: {p.SizeBytes} bytes vs Disk: {device.ReportedCapacityBytes} bytes",
                    RecommendedAction = IsRu ? "Восстановите разметку на максимально доступный объём флешки." : "Rebuild the partition using the maximum available drive capacity.",
                    CanAutoFix = true,
                    FixType = RepairActionType.CleanAndPartitionAll
                });
            }
        }
    }

    private void CheckFilesystemStatus(UsbDeviceInfo device, List<DiagnosticResult> results)
    {
        foreach (var part in device.Partitions)
        {
            // Check for missing drive letter
            if (part.DriveLetters.Count == 0 && part.PartitionType.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new DiagnosticResult
                {
                    Category = DiagnosticCategory.Filesystem,
                    Severity = HealthStatus.Warning,
                    Title = IsRu ? $"Разделу #{part.PartitionNumber} не назначена буква" : $"Partition #{part.PartitionNumber} Has No Drive Letter",
                    Description = IsRu ? "Раздел существует, но Windows не назначила ему букву диска, поэтому он не виден в Проводнике." : "The partition exists but is not mounted with a Windows drive letter, making it invisible in File Explorer.",
                    TechnicalDetails = $"Partition #{part.PartitionNumber}, Size: {part.SizeFormatted}",
                    RecommendedAction = IsRu ? "Назначить букву диска, чтобы флешка открывалась в «Мой компьютер»." : "Assign a drive letter to make this partition accessible in Explorer.",
                    CanAutoFix = true,
                    FixType = RepairActionType.AssignDriveLetter
                });
            }

            // Check for RAW filesystem
            if (part.Volume != null)
            {
                var fs = part.Volume.FileSystem.ToUpperInvariant();
                if (fs == "RAW" || fs == "UNKNOWN")
                {
                    results.Add(new DiagnosticResult
                    {
                        Category = DiagnosticCategory.Filesystem,
                        Severity = HealthStatus.Error,
                        Title = IsRu ? $"Поврежденная файловая система (RAW) на разделе #{part.PartitionNumber}" : $"RAW / Corrupted Filesystem on Partition #{part.PartitionNumber}",
                        Description = IsRu ? "Windows не может прочитать диск. Файлы недоступны." : "Windows cannot recognize the filesystem structure on this volume. Data cannot be read without formatting or filesystem repair.",
                        TechnicalDetails = $"Drive: {part.Volume.DriveLetter}, FileSystem: {part.Volume.FileSystem}",
                        RecommendedAction = IsRu ? "Выполнить форматирование в exFAT или запустить проверку chkdsk." : "Run filesystem repair or reformat the partition with exFAT/NTFS.",
                        CanAutoFix = true,
                        FixType = RepairActionType.FormatVolume
                    });
                }
                else if (fs == "FAT32" && part.SizeBytes > 32L * 1024 * 1024 * 1024)
                {
                    results.Add(new DiagnosticResult
                    {
                        Category = DiagnosticCategory.Filesystem,
                        Severity = HealthStatus.Warning,
                        Title = IsRu ? "FAT32 на большом накопителе (>32 ГБ)" : "FAT32 on Large Storage Volume (>32 GB)",
                        Description = IsRu ? "Том отформатирован в FAT32. В этой системе нельзя записать один файл размером более 4 ГБ (например, фильм или архив)." : $"Volume is formatted as FAT32 on a {part.SizeFormatted} partition. FAT32 cannot store single files larger than 4 GB and is inefficient on large drives.",
                        TechnicalDetails = $"FileSystem: FAT32, Size: {part.SizeFormatted}",
                        RecommendedAction = IsRu ? "Рекомендуется отформатировать в exFAT — современный стандарт для флешек без лимита на размер файла." : "Reformat with exFAT (modern cross-platform standard for flash memory) or NTFS.",
                        CanAutoFix = true,
                        FixType = RepairActionType.FormatVolume
                    });
                }
            }
        }
    }

    private void CheckDeviceHardwareStatus(UsbDeviceInfo device, List<DiagnosticResult> results)
    {
        // Read-only / Write Protection
        if (device.IsReadOnly)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.StorageDevice,
                Severity = HealthStatus.Error,
                Title = IsRu ? "Диск защищен от записи (Read-Only)" : "Device is in Read-Only Mode",
                Description = IsRu ? "На накопителе включен флаг защиты от записи. Нельзя сохранять, удалять или менять файлы." : "The drive has a read-only flag set. Files cannot be created, modified, or deleted.",
                TechnicalDetails = "Storage attribute: DISK_ATTRIBUTE_READONLY is set.",
                RecommendedAction = IsRu ? "Нажмите кнопку «Снять защиту от записи» в разделе восстановления." : "Clear read-only storage attribute via Windows Storage / Diskpart.",
                CanAutoFix = true,
                FixType = RepairActionType.ClearReadOnly
            });
        }

        // Offline status
        if (device.IsOffline)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.StorageDevice,
                Severity = HealthStatus.Error,
                Title = IsRu ? "Диск находится в состоянии Offline" : "Disk is Offline",
                Description = IsRu ? "Windows перевела этот накопитель в автономный режим, доступ к файлам заблокирован." : "Windows has placed this disk in an Offline state, preventing access to any volumes.",
                TechnicalDetails = "Disk state: Offline",
                RecommendedAction = IsRu ? "Перевести накопитель в режим Online." : "Bring the disk Online using Windows Storage Management.",
                CanAutoFix = true,
                FixType = RepairActionType.FixAll
            });
        }

        // SMART / Physical disk health
        if (device.Health == HealthStatus.Critical || device.Health == HealthStatus.Warning)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.StorageDevice,
                Severity = device.Health,
                Title = IsRu ? "Аппаратная деградация памяти (SMART)" : "Hardware Health Degradation Reported",
                Description = IsRu ? $"Подсистема хранения Windows сообщает об аппаратных проблемах с чипом памяти (Статус: {device.Health})." : $"Windows Storage Management reports degraded health for this physical disk (Status: {device.Health}).",
                TechnicalDetails = $"Operational Health: {device.Health}, SMART Status: {device.SmartStatus}",
                RecommendedAction = IsRu ? "Срочно скопируйте важные файлы. Контроллер или микросхема памяти выходят из строя." : "Backup data immediately. The flash controller or NAND cells may be failing physically.",
                CanAutoFix = false
            });
        }
    }

    private async Task CheckEventLogStatusAsync(UsbDeviceInfo device, List<DiagnosticResult> results)
    {
        try
        {
            var events = await _eventLogService.GetDiskRelatedEventsAsync(device.DiskIndex, TimeSpan.FromDays(3));
            device.RecentDeviceEvents = events;

            if (events.Count > 0)
            {
                device.EventLogSummary = IsRu ? $"{events.Count} ошибок/предупреждений в системном журнале Windows" : $"{events.Count} warning/error events recorded in Windows System Log";

                results.Add(new DiagnosticResult
                {
                    Category = DiagnosticCategory.StorageDevice,
                    Severity = HealthStatus.Warning,
                    Title = IsRu ? $"Ошибки в журнале Windows ({events.Count})" : $"Windows System Log Errors ({events.Count})",
                    Description = IsRu ? $"Windows зафиксировала {events.Count} недавних ошибок чтения/записи или сброса контроллера для этого диска." : $"Windows has logged {events.Count} recent storage/I/O warnings or errors related to this disk.",
                    TechnicalDetails = string.Join(Environment.NewLine, events.Take(5)),
                    RecommendedAction = IsRu ? "Проверьте USB-порт, кабель или запустите тест на битые сектора." : "Check USB port connection, cable, or run Full Capacity Test to check for bad sectors.",
                    CanAutoFix = false
                });
            }
            else
            {
                device.EventLogSummary = IsRu ? "Ошибок в журнале Windows не обнаружено" : "No recent errors found in Windows System Log";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(DiagnosticEngine), $"Failed event log check: {ex.Message}");
        }
    }

    private void CheckCapacityDiagnostics(UsbDeviceInfo device, List<DiagnosticResult> results)
    {
        if (device.ReportedCapacityBytes == 0)
        {
            results.Add(new DiagnosticResult
            {
                Category = DiagnosticCategory.Capacity,
                Severity = HealthStatus.Critical,
                Title = IsRu ? "Нулевой заявляемый объем (0 байт)" : "Zero Reported Capacity",
                Description = IsRu ? "Контроллер флешки сообщает емкость 0 байт. Контроллер завис в сервисном режиме BootROM или память повреждена аппаратно." : "The device controller reports 0 bytes capacity. The controller may be in firmware bootrom mode or flash memory is physically unreadable.",
                TechnicalDetails = "ReportedCapacity: 0 bytes",
                RecommendedAction = IsRu ? "Попробуйте подключить к разъему на задней панели ПК. Если не поможет — требуется аппаратный ремонт." : "Try reconnecting to a direct motherboard USB port. If issue persists, hardware recovery is required.",
                CanAutoFix = false
            });
        }
    }

    #endregion
}
