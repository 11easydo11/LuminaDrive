using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Logging;
using UsbRecoverySuite.Core.Services.Storage;

namespace UsbRecoverySuite.Core.Services.DeviceDetection;

public class WindowsDeviceDetectorService : IDeviceDetectorService
{
    private readonly IWindowsStorageService _storageService;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();
    private List<UsbDeviceInfo> _cachedDevices = new();
    private System.Threading.Timer? _pollTimer;
    private bool _isMonitoring;

    public event Action<UsbDeviceInfo>? DeviceArrived;
    public event Action<int>? DeviceRemoved;
    public event Action<IReadOnlyList<UsbDeviceInfo>>? DevicesRefreshed;

    public WindowsDeviceDetectorService(IWindowsStorageService storageService, IAppLogger logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    public IReadOnlyList<UsbDeviceInfo> ConnectedDevices
    {
        get
        {
            lock (_lock)
            {
                return _cachedDevices.ToList();
            }
        }
    }

    public IReadOnlyList<UsbDeviceInfo> UsbDevicesOnly
    {
        get
        {
            lock (_lock)
            {
                return _cachedDevices.Where(d => d.DeviceType != UsbStorageDeviceType.InternalDisk).ToList();
            }
        }
    }

    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        _isMonitoring = true;
        _logger.LogInfo(nameof(WindowsDeviceDetectorService), "USB hotplug monitoring started.");

        // Initial scan
        _ = RefreshDevicesAsync();

        // Background heartbeat poll (every 4 seconds) to ensure detection if window message is missed
        _pollTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                await RefreshDevicesAsync();
            }
            catch
            {
                // Suppress timer exceptions
            }
        }, null, 4000, 4000);
    }

    public void StopMonitoring()
    {
        _isMonitoring = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _logger.LogInfo(nameof(WindowsDeviceDetectorService), "USB hotplug monitoring stopped.");
    }

    public void HandleDeviceChangeNotification(int eventType)
    {
        // 0x8000 = DBT_DEVICEARRIVAL, 0x8004 = DBT_DEVICEREMOVECOMPLETE, 0x0007 = DBT_DEVNODES_CHANGED
        _logger.LogInfo(nameof(WindowsDeviceDetectorService), $"Received WM_DEVICECHANGE event code: 0x{eventType:X}");
        _ = Task.Run(async () =>
        {
            // Give Windows storage stack 600ms to mount before enumerating
            await Task.Delay(600);
            await RefreshDevicesAsync();
        });
    }

    public async Task RefreshDevicesAsync()
    {
        try
        {
            var freshDisks = await _storageService.GetAllDisksAsync();

            List<UsbDeviceInfo> oldDisks;
            lock (_lock)
            {
                oldDisks = _cachedDevices.ToList();
            }

            // Identify arrivals
            foreach (var newDisk in freshDisks)
            {
                if (!oldDisks.Any(o => o.DiskIndex == newDisk.DiskIndex))
                {
                    _logger.LogSuccess(nameof(WindowsDeviceDetectorService), 
                        $"Device arrived: Disk {newDisk.DiskIndex} - {newDisk.Model} ({newDisk.DeviceTypeDisplay})");
                    DeviceArrived?.Invoke(newDisk);
                }
            }

            // Identify removals
            foreach (var oldDisk in oldDisks)
            {
                if (!freshDisks.Any(n => n.DiskIndex == oldDisk.DiskIndex))
                {
                    _logger.LogWarning(nameof(WindowsDeviceDetectorService), 
                        $"Device removed: Disk {oldDisk.DiskIndex} - {oldDisk.Model}");
                    DeviceRemoved?.Invoke(oldDisk.DiskIndex);
                }
            }

            lock (_lock)
            {
                _cachedDevices = freshDisks;
            }

            DevicesRefreshed?.Invoke(freshDisks);
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(WindowsDeviceDetectorService), $"Error refreshing devices: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
