namespace UsbRecoverySuite.Core.Services.Diagnostics;

public interface IWindowsEventLogService
{
    Task<List<string>> GetDiskRelatedEventsAsync(int diskIndex, TimeSpan? timeSpan = null);
}
