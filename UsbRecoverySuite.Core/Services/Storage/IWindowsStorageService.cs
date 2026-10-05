using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Storage;

public interface IWindowsStorageService
{
    Task<List<UsbDeviceInfo>> GetAllDisksAsync();
    Task<UsbDeviceInfo?> GetDiskByIndexAsync(int diskIndex);
    Task<bool> RescanStorageBusAsync();
}
