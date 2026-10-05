using System.Text.Json;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Logging;

namespace UsbRecoverySuite.Core.Services.History;

public class HistoryService : IHistoryService
{
    private readonly IAppLogger _logger;
    private readonly List<OperationHistoryItem> _items = new();
    private readonly object _lock = new();
    private readonly string _historyFilePath;

    public event Action<OperationHistoryItem>? OnHistoryItemAdded;

    public HistoryService(IAppLogger logger)
    {
        _logger = logger;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbRecoverySuite");
        Directory.CreateDirectory(dir);
        _historyFilePath = Path.Combine(dir, "history.json");
        LoadHistory();
    }

    public IReadOnlyList<OperationHistoryItem> GetHistory()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(i => i.Timestamp).ToList();
        }
    }

    public async Task AddHistoryEntryAsync(int diskIndex, string model, string serial, string operation, bool success, string details)
    {
        var item = new OperationHistoryItem
        {
            Timestamp = DateTime.Now,
            DiskIndex = diskIndex,
            DiskModel = model,
            DiskSerial = serial,
            OperationName = operation,
            Success = success,
            Details = details
        };

        lock (_lock)
        {
            _items.Insert(0, item);
            if (_items.Count > 1000) _items.RemoveAt(_items.Count - 1);
        }

        try
        {
            OnHistoryItemAdded?.Invoke(item);
        }
        catch { }

        await SaveHistoryAsync();
    }

    public async Task ClearHistoryAsync()
    {
        lock (_lock)
        {
            _items.Clear();
        }
        await SaveHistoryAsync();
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                string json = File.ReadAllText(_historyFilePath);
                var loaded = JsonSerializer.Deserialize<List<OperationHistoryItem>>(json);
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        _items.Clear();
                        _items.AddRange(loaded);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(HistoryService), $"Failed to load history: {ex.Message}");
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            }
            await File.WriteAllTextAsync(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(HistoryService), $"Failed to save history: {ex.Message}");
        }
    }
}
