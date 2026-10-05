using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.History;

public interface IHistoryService
{
    event Action<OperationHistoryItem>? OnHistoryItemAdded;
    IReadOnlyList<OperationHistoryItem> GetHistory();
    Task AddHistoryEntryAsync(int diskIndex, string model, string serial, string operation, bool success, string details);
    Task ClearHistoryAsync();
}
