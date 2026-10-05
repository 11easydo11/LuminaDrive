using System.Text;
using System.Text.Json;
using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Reporting;

public class ReportService : IReportService
{
    public string GenerateTextReport(
        UsbDeviceInfo device,
        IEnumerable<DiagnosticResult> diagnostics,
        CapacityTestResult? capacityTest = null,
        RepairOperationResult? repairResult = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("          LUMINA DRIVE — USB RECOVERY & DIAGNOSTIC SUITE REPORT");
        sb.AppendLine($" Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | OS: {Environment.OSVersion.VersionString}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        sb.AppendLine("1. DEVICE IDENTIFICATION");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"  Physical Disk Index : Disk {device.DiskIndex}");
        sb.AppendLine($"  Model               : {device.Model}");
        sb.AppendLine($"  Manufacturer        : {device.Manufacturer}");
        sb.AppendLine($"  Serial Number       : {device.SerialNumber}");
        sb.AppendLine($"  Device ID           : {device.DeviceId}");
        sb.AppendLine($"  PNP Device ID       : {device.PnpDeviceId}");
        sb.AppendLine($"  Vendor / Product ID : VID_{device.Vid} / PID_{device.Pid}");
        sb.AppendLine($"  Bus Interface       : {device.BusType}");
        sb.AppendLine($"  USB Version / Speed : {device.UsbVersionSpeed}");
        sb.AppendLine($"  Firmware Revision   : {device.FirmwareRevision}");
        sb.AppendLine($"  Classification      : {device.DeviceTypeDisplay}");
        sb.AppendLine($"  Removable Media     : {(device.IsRemovable ? "Yes" : "No (Fixed)")}");
        sb.AppendLine($"  Read-Only Status    : {(device.IsReadOnly ? "Yes (Write-Protected)" : "No (Writable)")}");
        sb.AppendLine($"  Offline Status      : {(device.IsOffline ? "Yes" : "No (Online)")}");
        sb.AppendLine($"  Partition Style     : {device.PartitionStyle}");
        sb.AppendLine($"  Sector Sizes        : Logical: {device.LogicalSectorSize} B, Physical: {device.PhysicalSectorSize} B");
        sb.AppendLine($"  Reported Capacity   : {device.ReportedCapacityFormatted} ({device.ReportedCapacityBytes:N0} bytes)");
        sb.AppendLine($"  Total Partitioned   : {device.TotalPartitionedFormatted} ({device.TotalPartitionedBytes:N0} bytes)");
        sb.AppendLine($"  Unallocated Space   : {device.UnallocatedFormatted} ({device.UnallocatedPercent:F1}%)");
        sb.AppendLine();

        sb.AppendLine("2. PARTITION & VOLUME LAYOUT");
        sb.AppendLine("--------------------------------------------------------------------------------");
        if (device.Partitions.Count == 0)
        {
            sb.AppendLine("  No partitions found (Disk is uninitialized or RAW).");
        }
        else
        {
            foreach (var p in device.Partitions)
            {
                sb.AppendLine($"  Partition #{p.PartitionNumber}:");
                sb.AppendLine($"    Size            : {p.SizeFormatted} ({p.SizeBytes:N0} bytes)");
                sb.AppendLine($"    Offset          : {p.OffsetFormatted}");
                sb.AppendLine($"    Type            : {p.PartitionType}");
                sb.AppendLine($"    Drive Letters   : {p.DriveLettersDisplay}");
                if (p.Volume != null)
                {
                    sb.AppendLine($"    Volume Label    : {p.Volume.VolumeLabel}");
                    sb.AppendLine($"    File System     : {p.Volume.FileSystem}");
                    sb.AppendLine($"    Free / Total    : {p.Volume.FreeFormatted} free of {p.Volume.TotalFormatted}");
                }
            }
        }
        sb.AppendLine();

        sb.AppendLine("3. DIAGNOSTIC RESULTS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        var diagList = diagnostics.ToList();
        if (diagList.Count == 0)
        {
            sb.AppendLine("  Diagnostics passed with zero errors or warnings.");
        }
        else
        {
            foreach (var d in diagList)
            {
                sb.AppendLine($"  [{d.SeverityBadge}] [{d.Category}] {d.Title}");
                sb.AppendLine($"    Description : {d.Description}");
                if (!string.IsNullOrWhiteSpace(d.TechnicalDetails))
                    sb.AppendLine($"    Details     : {d.TechnicalDetails}");
                sb.AppendLine($"    Action      : {d.RecommendedAction}");
                sb.AppendLine();
            }
        }

        if (capacityTest != null)
        {
            sb.AppendLine("4. FULL CAPACITY TEST RESULTS");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"  Reported Capacity   : {capacityTest.ReportedFormatted}");
            sb.AppendLine($"  Addressable Capacity: {capacityTest.AddressableFormatted}");
            sb.AppendLine($"  Verified Capacity   : {capacityTest.VerifiedFormatted}");
            sb.AppendLine($"  Reliable Capacity   : {capacityTest.ReliableFormatted}");
            sb.AppendLine($"  Read Speed          : {capacityTest.AverageReadSpeedMBps:F2} MB/s");
            sb.AppendLine($"  Write Speed         : {capacityTest.AverageWriteSpeedMBps:F2} MB/s");
            sb.AppendLine($"  Write Errors        : {capacityTest.WriteErrorsCount}");
            sb.AppendLine($"  Read/Verify Errors  : {capacityTest.ReadErrorsCount}");
            sb.AppendLine($"  Suspected Fake      : {(capacityTest.FakeDriveSuspected ? $"YES ({capacityTest.FakeDriveConfidencePercent}% confidence)" : "NO")}");
            if (capacityTest.FakeDriveSuspected)
                sb.AppendLine($"  Evidence            : {capacityTest.FakeDriveEvidence}");
            sb.AppendLine($"  Summary             : {capacityTest.Summary}");
            sb.AppendLine($"  {CapacityTestResult.CapacityWarningNotice}");
            sb.AppendLine();
        }

        if (repairResult != null)
        {
            sb.AppendLine("5. REPAIR & RECOVERY VERIFICATION");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"  Operation           : {repairResult.OperationName}");
            sb.AppendLine($"  Executed At         : {repairResult.Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Status              : {(repairResult.Success ? "SUCCESS" : "FAILED")}");
            sb.AppendLine($"  Verified Status     : {(repairResult.Verified ? "VERIFIED VALID" : "UNVERIFIED / INCOMPLETE")}");
            sb.AppendLine($"  Summary             : {repairResult.VerificationSummary}");
            if (repairResult.BeforeSnapshot != null && repairResult.AfterSnapshot != null)
            {
                sb.AppendLine("  BEFORE / AFTER COMPARISON:");
                sb.AppendLine($"    Partition count   : {repairResult.BeforeSnapshot.Partitions.Count} -> {repairResult.AfterSnapshot.Partitions.Count}");
                sb.AppendLine($"    Partitioned space : {repairResult.BeforeSnapshot.TotalPartitionedFormatted} -> {repairResult.AfterSnapshot.TotalPartitionedFormatted}");
                sb.AppendLine($"    Unallocated space : {repairResult.BeforeSnapshot.UnallocatedFormatted} -> {repairResult.AfterSnapshot.UnallocatedFormatted}");
                sb.AppendLine($"    Primary FS        : {repairResult.BeforeSnapshot.PrimaryFileSystem} -> {repairResult.AfterSnapshot.PrimaryFileSystem}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                     END OF USB SUITE DIAGNOSTIC REPORT");
        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    public string GenerateJsonReport(
        UsbDeviceInfo device,
        IEnumerable<DiagnosticResult> diagnostics,
        CapacityTestResult? capacityTest = null,
        RepairOperationResult? repairResult = null)
    {
        var data = new
        {
            Metadata = new
            {
                Application = "Lumina Drive — USB Recovery & Diagnostic Suite",
                Version = "1.0.0",
                Timestamp = DateTime.UtcNow,
                Platform = "Windows 10/11 x64"
            },
            Device = device,
            Diagnostics = diagnostics,
            CapacityTest = capacityTest,
            Repair = repairResult,
            Notice = CapacityTestResult.CapacityWarningNotice
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public string GenerateHtmlReport(
        UsbDeviceInfo device,
        IEnumerable<DiagnosticResult> diagnostics,
        CapacityTestResult? capacityTest = null,
        RepairOperationResult? repairResult = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine($"  <title>Diagnostic Report - {Escape(device.Model)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root {");
        sb.AppendLine("      --bg: #0d1117; --card-bg: #161b22; --border: #30363d; --text: #e6edf3;");
        sb.AppendLine("      --text-muted: #8b949e; --accent: #58a6ff; --success: #238636; --warning: #d29922; --danger: #da3633;");
        sb.AppendLine("    }");
        sb.AppendLine("    body { font-family: 'Segoe UI Variable', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: var(--bg); color: var(--text); padding: 28px; margin: 0; line-height: 1.5; }");
        sb.AppendLine("    .container { max-width: 960px; margin: 0 auto; }");
        sb.AppendLine("    .header { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--border); padding-bottom: 20px; margin-bottom: 24px; }");
        sb.AppendLine("    .title { font-size: 24px; font-weight: 600; color: var(--accent); margin: 0; }");
        sb.AppendLine("    .meta { font-size: 13px; color: var(--text-muted); }");
        sb.AppendLine("    .card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 8px; padding: 20px; margin-bottom: 20px; }");
        sb.AppendLine("    .card-title { font-size: 16px; font-weight: 600; margin-top: 0; margin-bottom: 16px; border-bottom: 1px solid #21262d; padding-bottom: 8px; }");
        sb.AppendLine("    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 14px; }");
        sb.AppendLine("    .field-label { font-size: 12px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.5px; }");
        sb.AppendLine("    .field-value { font-size: 14px; font-weight: 500; word-break: break-word; }");
        sb.AppendLine("    .badge { display: inline-block; padding: 3px 8px; border-radius: 12px; font-size: 11px; font-weight: 700; }");
        sb.AppendLine("    .badge-ok { background: #23863633; color: #3fb950; border: 1px solid #238636; }");
        sb.AppendLine("    .badge-warning { background: #d2992233; color: #f2cc60; border: 1px solid #d29922; }");
        sb.AppendLine("    .badge-error { background: #da363333; color: #f85149; border: 1px solid #da3633; }");
        sb.AppendLine("    .badge-critical { background: #8e1519; color: #ffffff; border: 1px solid #f85149; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin-top: 10px; font-size: 13px; }");
        sb.AppendLine("    th, td { text-align: left; padding: 10px 12px; border-bottom: 1px solid var(--border); }");
        sb.AppendLine("    th { background: #21262d; color: var(--text-muted); font-size: 11px; text-transform: uppercase; }");
        sb.AppendLine("    .notice { background: #1c2738; border-left: 4px solid var(--accent); padding: 12px 16px; border-radius: 4px; margin-top: 20px; font-size: 13px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        // Header
        sb.AppendLine("  <div class=\"header\">");
        sb.AppendLine("    <div>");
        sb.AppendLine("      <h1 class=\"title\">Lumina Drive — USB Recovery &amp; Diagnostic Suite</h1>");
        sb.AppendLine("      <div class=\"meta\">Automated Hardware &amp; Filesystem Diagnostic Report</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine($"    <div class=\"meta\">{DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("  </div>");

        // Device card
        sb.AppendLine("  <div class=\"card\">");
        sb.AppendLine($"    <h2 class=\"card-title\">Device Identification: Disk {device.DiskIndex}</h2>");
        sb.AppendLine("    <div class=\"grid\">");
        sb.AppendLine($"      <div><div class=\"field-label\">Model</div><div class=\"field-value\">{Escape(device.Model)}</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Vendor / Product ID</div><div class=\"field-value\">VID_{device.Vid} / PID_{device.Pid}</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Serial Number</div><div class=\"field-value\">{Escape(device.SerialNumber)}</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Reported Capacity</div><div class=\"field-value\">{device.ReportedCapacityFormatted}</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Bus Type / Speed</div><div class=\"field-value\">{device.BusType} ({device.UsbVersionSpeed})</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Device Type</div><div class=\"field-value\">{device.DeviceTypeDisplay}</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Partition Style</div><div class=\"field-value\">{device.PartitionStyle}</div></div>");
        sb.AppendLine($"      <div><div class=\"field-label\">Unallocated Space</div><div class=\"field-value\">{device.UnallocatedFormatted} ({device.UnallocatedPercent:F1}%)</div></div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");

        // Diagnostics card
        sb.AppendLine("  <div class=\"card\">");
        sb.AppendLine("    <h2 class=\"card-title\">Diagnostics & Health Findings</h2>");
        var diagList = diagnostics.ToList();
        if (diagList.Count == 0)
        {
            sb.AppendLine("    <p style=\"color: #3fb950;\">No problems or anomalies detected. Device passed all diagnostic checks.</p>");
        }
        else
        {
            sb.AppendLine("    <table>");
            sb.AppendLine("      <thead><tr><th>Severity</th><th>Category</th><th>Issue</th><th>Recommended Action</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            foreach (var d in diagList)
            {
                string bClass = d.Severity switch
                {
                    HealthStatus.Ok => "badge-ok",
                    HealthStatus.Warning => "badge-warning",
                    HealthStatus.Error => "badge-error",
                    HealthStatus.Critical => "badge-critical",
                    _ => "badge-warning"
                };
                sb.AppendLine($"      <tr>");
                sb.AppendLine($"        <td><span class=\"badge {bClass}\">{d.SeverityBadge}</span></td>");
                sb.AppendLine($"        <td>{d.Category}</td>");
                sb.AppendLine($"        <td><strong>{Escape(d.Title)}</strong><br/><small style=\"color: var(--text-muted);\">{Escape(d.Description)}</small></td>");
                sb.AppendLine($"        <td>{Escape(d.RecommendedAction)}</td>");
                sb.AppendLine($"      </tr>");
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
        }
        sb.AppendLine("  </div>");

        // Capacity test card
        if (capacityTest != null)
        {
            sb.AppendLine("  <div class=\"card\">");
            sb.AppendLine("    <h2 class=\"card-title\">Full Capacity & Fake Drive Test</h2>");
            sb.AppendLine("    <div class=\"grid\">");
            sb.AppendLine($"      <div><div class=\"field-label\">Reported</div><div class=\"field-value\">{capacityTest.ReportedFormatted}</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Addressable</div><div class=\"field-value\">{capacityTest.AddressableFormatted}</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Verified</div><div class=\"field-value\">{capacityTest.VerifiedFormatted}</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Reliable</div><div class=\"field-value\">{capacityTest.ReliableFormatted}</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Average Write Speed</div><div class=\"field-value\">{capacityTest.AverageWriteSpeedMBps:F2} MB/s</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Average Read Speed</div><div class=\"field-value\">{capacityTest.AverageReadSpeedMBps:F2} MB/s</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Errors Found</div><div class=\"field-value\">{capacityTest.TotalErrorsCount}</div></div>");
            sb.AppendLine($"      <div><div class=\"field-label\">Suspected Fake</div><div class=\"field-value\">{(capacityTest.FakeDriveSuspected ? "YES (HIGH RISK)" : "NO")}</div></div>");
            sb.AppendLine("    </div>");
            sb.AppendLine($"    <p style=\"margin-top: 15px; font-weight: 500;\">{Escape(capacityTest.Summary)}</p>");
            sb.AppendLine("  </div>");
        }

        // Repair card
        if (repairResult != null)
        {
            sb.AppendLine("  <div class=\"card\">");
            sb.AppendLine($"    <h2 class=\"card-title\">Repair Action: {Escape(repairResult.OperationName)}</h2>");
            sb.AppendLine($"    <p>Status: <span class=\"badge {(repairResult.Success ? "badge-ok" : "badge-error")}\">{(repairResult.Success ? "SUCCESS" : "FAILED")}</span> ");
            sb.AppendLine($"       Verification: <span class=\"badge {(repairResult.Verified ? "badge-ok" : "badge-warning")}\">{(repairResult.Verified ? "VERIFIED" : "UNVERIFIED")}</span></p>");
            sb.AppendLine($"    <p>{Escape(repairResult.VerificationSummary)}</p>");
            sb.AppendLine("  </div>");
        }

        // Notice
        sb.AppendLine("  <div class=\"notice\">");
        sb.AppendLine($"    <strong>Important:</strong> {CapacityTestResult.CapacityWarningNotice}");
        sb.AppendLine("  </div>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    public async Task ExportReportAsync(string filePath, string content)
    {
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
    }

    private string Escape(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text ?? string.Empty);
    }
}
