using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// One-line descriptions of a record the database refuses, for the warning logged when a batch is split down to it:
/// signal-specific identity (trace/span id, log time and severity, metric name) plus tenant and service.
/// </summary>
public static class DroppedRecordDescription
{
    public static string Of(object record) => record switch
    {
        SpanModel s => $"span {s.TraceIdHex}/{s.SpanIdHex} '{Clip(s.Name)}'{Where(s.Resource)}",
        LogRecordModel l => $"log at {l.TimeUnixNano ?? l.ObservedTimeUnixNano} severity {l.SeverityNumber}{Where(l.Resource)}",
        MetricModel m => $"metric '{Clip(m.Name)}'{Where(m.Resource)}",
        _ => record.GetType().Name
    };

    private static string Where(ResourceModel? r) =>
        r is null ? "" : $" (tenant {r.TenantId}, service {(r.Attributes.TryGetValue("service.name", out var v) ? v : "unknown")})";

    private static string Clip(string? s) => s is null ? "" : s.Length <= 80 ? s : s[..80] + "...";
}
