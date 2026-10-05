using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Logging;

public interface IAppLogger
{
    event Action<AppLogEntry>? OnLogEntry;
    IReadOnlyList<AppLogEntry> Entries { get; }
    void Log(LogLevel level, string source, string message);
    void LogInfo(string source, string message);
    void LogSuccess(string source, string message);
    void LogWarning(string source, string message);
    void LogError(string source, string message);
    void LogDebug(string source, string message);
    void Clear();
    string GetLogsAsText();
}
