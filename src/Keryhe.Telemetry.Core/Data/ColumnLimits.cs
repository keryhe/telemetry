namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// The sizes of the sized text columns, in characters, as the smallest across the PostgreSQL, SQL Server and MySQL
/// telemetry schemas (ClickHouse's columns are unbounded strings). A value longer than its column is a permanent flush
/// error on those providers, so conversion clips to these sizes on every provider and a record looks the same everywhere.
/// <c>ColumnLimitsTests</c> parses the three scripts and fails when a sized column is missing here or differs, so a schema
/// change cannot silently reintroduce the failure.
/// </summary>
public static class ColumnLimits
{
    /// <summary>The <c>service_name</c> columns of resources, spans, log records, metrics and the rollup tables (derived from the <c>service.name</c> resource attribute).</summary>
    public const int ServiceName = 255;
    public const int SpanName = 255;
    public const int ScopeName = 255;
    public const int ScopeVersion = 255;
    public const int MetricName = 255;
    public const int MetricUnit = 63;
    public const int EventName = 256;
    public const int SeverityText = 255;
    public const int SchemaUrl = 2048;

    /// <summary>Every limit by "table.column" name, for the schema test.</summary>
    public static IReadOnlyDictionary<string, int> ByColumn { get; } = new Dictionary<string, int>
    {
        ["resources.service_name"] = ServiceName,
        ["resources.schema_url"] = SchemaUrl,
        ["instrumentation_scopes.name"] = ScopeName,
        ["instrumentation_scopes.version"] = ScopeVersion,
        ["instrumentation_scopes.schema_url"] = SchemaUrl,
        ["spans.service_name"] = ServiceName,
        ["spans.name"] = SpanName,
        ["metrics.service_name"] = ServiceName,
        ["metrics.name"] = MetricName,
        ["metrics.unit"] = MetricUnit,
        ["log_records.service_name"] = ServiceName,
        ["log_records.event_name"] = EventName,
        ["log_records.severity_text"] = SeverityText,
        ["request_rollup_minute.service_name"] = ServiceName,
        ["log_rollup_minute.service_name"] = ServiceName,
        ["request_rollup_hour.service_name"] = ServiceName,
        ["log_rollup_hour.service_name"] = ServiceName,
    };

    /// <summary>
    /// Clips <paramref name="value"/> to <paramref name="max"/> UTF-16 characters (what NVARCHAR(n) and VARCHAR(n) count)
    /// without splitting a surrogate pair. Null stays null; <paramref name="max"/> of 0 or less means unlimited.
    /// </summary>
    public static string? Clip(string? value, int max, out bool clipped)
    {
        clipped = false;
        if (value is null || max <= 0 || value.Length <= max) return value;
        clipped = true;
        var end = max;
        if (char.IsHighSurrogate(value[end - 1])) end--;   // the pair would be cut in two
        return value[..end];
    }
}
