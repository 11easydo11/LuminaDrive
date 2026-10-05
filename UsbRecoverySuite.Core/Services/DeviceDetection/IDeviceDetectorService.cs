using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.DeviceDetection;

public interface IDeviceDetectorService : IDisposable
{
    event Action<UsbDeviceInfo>? DeviceArrived;
    event Action<int>? DeviceRemoved;
    event Action<IReadOnlyList<UsbDeviceInfo>>? DevicesRefreshed;

    IReadOnlyList<UsbDeviceInfo> ConnectedDevices { get; }
    IReadOnlyList<UsbDeviceInfo> UsbDevicesOnly { get; }

    Task RefreshDevicesAsync();
    void HandleDeviceChangeNotification(int eventType);
    void StartMonitoring();
    void StopMonitoring();
}
