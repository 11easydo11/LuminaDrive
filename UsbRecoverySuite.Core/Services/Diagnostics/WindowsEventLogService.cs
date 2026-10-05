using System.Diagnostics.Eventing.Reader;
using UsbRecoverySuite.Core.Services.Logging;

namespace UsbRecoverySuite.Core.Services.Diagnostics;

public class WindowsEventLogService : IWindowsEventLogService
{
    private readonly IAppLogger _logger;

    public WindowsEventLogService(IAppLogger logger)
    {
        _logger = logger;
    }

    public Task<List<string>> GetDiskRelatedEventsAsync(int diskIndex, TimeSpan? timeSpan = null)
    {
        return Task.Run(() =>
        {
            var results = new List<string>();
            var window = timeSpan ?? TimeSpan.FromDays(3);
            long milliseconds = (long)window.TotalMilliseconds;

            string[] providers = { "disk", "partmgr", "stornvme", "usbstor", "volsnap", "Ntfs", "exfat", "UASPStor" };
            string providerFilter = string.Join(" or ", providers.Select(p => $"@Name='{p}'"));

            // Query System log for errors (Level 1, 2) and warnings (Level 3)
            string queryString = $"*[System[Provider[{providerFilter}] and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";

            try
            {
                var query = new EventLogQuery("System", PathType.LogName, queryString)
                {
                    ReverseDirection = true
                };

                using var reader = new EventLogReader(query);
                EventRecord? record;
                int count = 0;

                string diskTarget1 = $"\\Device\\Harddisk{diskIndex}\\";
                string diskTarget2 = $"Harddisk{diskIndex}";
                string diskTarget3 = $"DR{diskIndex}";

                while ((record = reader.ReadEvent()) != null && count < 30)
                {
                    using (record)
                    {
                        try
                        {
                            string? message = record.FormatDescription();
                            string provider = record.ProviderName ?? "Storage";
                            int eventId = record.Id;
                            DateTime? time = record.TimeCreated;

                            // Check if this event relates to this specific disk or general USB/storage failure
                            bool isMatch = false;
                            if (!string.IsNullOrEmpty(message))
                            {
                                if (message.Contains(diskTarget1, StringComparison.OrdinalIgnoreCase) ||
                                    message.Contains(diskTarget2, StringComparison.OrdinalIgnoreCase) ||
                                    message.Contains(diskTarget3, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }
                                else if (provider.Equals("usbstor", StringComparison.OrdinalIgnoreCase) || 
                                         provider.Equals("UASPStor", StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }
                            }

                            if (isMatch)
                            {
                                string cleanMsg = message?.Replace("\r\n", " ").Trim() ?? "No description";
                                if (cleanMsg.Length > 180)
                                {
                                    cleanMsg = cleanMsg.Substring(0, 180) + "...";
                                }

                                string level = record.Level switch
                                {
                                    1 => "CRITICAL",
                                    2 => "ERROR",
                                    3 => "WARNING",
                                    _ => "INFO"
                                };

                                results.Add($"[{time:yyyy-MM-dd HH:mm}] [{level}] [{provider} #{eventId}]: {cleanMsg}");
                                count++;
                            }
                        }
                        catch
                        {
                            // Skip formatting errors on individual records
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(nameof(WindowsEventLogService), $"Unable to query Event Log for Disk {diskIndex}: {ex.Message}");
            }

            return results;
        });
    }
}
