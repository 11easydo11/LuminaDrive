using System.Text;
using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Logging;

public class AppLogger : IAppLogger
{
    private readonly List<AppLogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly string? _logFilePath;

    public event Action<AppLogEntry>? OnLogEntry;

    public AppLogger()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsbRecoverySuite",
                "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _logFilePath = null;
        }
    }

    public IReadOnlyList<AppLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public void Log(LogLevel level, string source, string message)
    {
        var entry = new AppLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Source = source,
            Message = message
        };

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > 5000)
            {
                _entries.RemoveAt(0);
            }
        }

        if (_logFilePath != null)
        {
            try
            {
                File.AppendAllText(_logFilePath, entry.FormattedLine + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Silently ignore disk write issues for logging
            }
        }

        try
        {
            OnLogEntry?.Invoke(entry);
        }
        catch
        {
            // Do not break execution on event handler exceptions
        }
    }

    public void LogInfo(string source, string message) => Log(LogLevel.Info, source, message);
    public void LogSuccess(string source, string message) => Log(LogLevel.Success, source, message);
    public void LogWarning(string source, string message) => Log(LogLevel.Warning, source, message);
    public void LogError(string source, string message) => Log(LogLevel.Error, source, message);
    public void LogDebug(string source, string message) => Log(LogLevel.Debug, source, message);

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public string GetLogsAsText()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var entry in _entries)
            {
                sb.AppendLine(entry.FormattedLine);
            }
            return sb.ToString();
        }
    }
}
