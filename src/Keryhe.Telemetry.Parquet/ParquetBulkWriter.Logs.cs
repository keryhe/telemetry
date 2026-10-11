using DuckDB.NET.Data;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Parquet;

public sealed partial class ParquetBulkWriter
{
    public Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken cancellationToken = default)
        => FlushLogsAsync(records, NewToken(), cancellationToken);

    public async Task FlushLogsAsync(List<LogRecordModel> records, string token, CancellationToken ct = default)
    {
        if (records.Count == 0) return;
        ValidateToken(token);

        var context = new FlushContext();
        var nowNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var rows = new List<LogRow>(records.Count);
        foreach (var record in records) rows.Add(BuildLogRow(record, context, nowNano));

        await WriteRowsAsync(ParquetTables.LogTable, rows, r => r.TenantId, r => r.Time, AppendLog, token, ct);
        await WriteDerivedAsync(ParquetTables.LogRollupTable, LogRollupRows(rows), r => r.TenantId, r => r.Minute, AppendLogRollup, token, ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    /// <summary>A log's partition time and observed time: a missing timestamp falls back to the observed one, then to now.</summary>
    private static (long Time, long Observed) LogTimes(LogRecordModel r, long nowNano)
    {
        var observed = r.ObservedTimeUnixNano is > 0 ? r.ObservedTimeUnixNano.Value : 0L;
        var time = r.TimeUnixNano is > 0 ? r.TimeUnixNano.Value : observed;
        if (time == 0) time = observed = nowNano;
        if (observed == 0) observed = time;
        return (time, observed);
    }

    private sealed record LogRow(
        long TenantId, string Service, long Time, long Observed, ulong TraceHi, ulong TraceLo, ulong SpanId, ulong Flags,
        byte Severity, short RollupSeverity, string SeverityText, string EventName, string Body, string AttributesJson,
        ulong Dropped, ResourceInfo Resource, ScopeInfo Scope);

    private static LogRow BuildLogRow(LogRecordModel r, FlushContext context, long nowNano)
    {
        var resource = context.Resource(r.Resource);
        var scope = context.Scope(r.InstrumentationScope);
        var (time, observed) = LogTimes(r, nowNano);
        var (hi, lo) = ParquetIds.TraceId(r.TraceIdHex);
        return new LogRow(
            resource.TenantId, resource.ServiceName, time, observed, hi, lo, ParquetIds.SpanId(r.SpanIdHex), ToUInt64(r.Flags),
            (byte)Math.Clamp(r.SeverityNumber ?? 0, 0, 255), (short)(r.SeverityNumber ?? -1), r.SeverityText ?? "", r.EventName ?? "",
            r.BodyValue ?? "", ParquetAttributes.ToJson(r.Attributes), ToUInt64(r.DroppedAttributesCount), resource, scope);
    }

    private static void AppendLog(LogRow r, IDuckDBAppenderRow row)
        // The order is ParquetTables.LogTable's.
        => row.AppendValue(r.TenantId).AppendValue(r.Service).AppendValue(r.Time).AppendValue(r.Observed)
            .AppendValue(r.TraceHi).AppendValue(r.TraceLo).AppendValue(r.SpanId).AppendValue(r.Flags)
            .AppendValue(r.Severity).AppendValue(r.SeverityText).AppendValue(r.EventName).AppendValue(r.Body)
            .AppendValue(r.AttributesJson).AppendValue(r.Dropped)
            .AppendValue(r.Resource.AttributesJson).AppendValue(r.Resource.SchemaUrl)
            .AppendValue(r.Scope.Name).AppendValue(r.Scope.Version).AppendValue(r.Scope.AttributesJson).AppendValue(r.Scope.SchemaUrl);

    private sealed record LogRollupRow(long TenantId, string Service, long Minute, short Severity, long Count);

    /// <summary>Log records per (tenant, service, minute, severity); an unspecified severity is -1, as in the relational rollup.</summary>
    private static List<LogRollupRow> LogRollupRows(List<LogRow> logs)
    {
        var folds = new Dictionary<(long Tenant, string Service, long Minute, short Severity), long>();
        foreach (var r in logs)
        {
            var key = (r.TenantId, r.Service, MinuteOf(r.Time), r.RollupSeverity);
            folds[key] = folds.GetValueOrDefault(key) + 1;
        }
        return folds.Select(kv => new LogRollupRow(kv.Key.Tenant, kv.Key.Service, kv.Key.Minute, kv.Key.Severity, kv.Value)).ToList();
    }

    private static void AppendLogRollup(LogRollupRow r, IDuckDBAppenderRow row)
        => row.AppendValue(r.TenantId).AppendValue(r.Service).AppendValue(r.Minute).AppendValue(r.Severity).AppendValue(r.Count);
}
