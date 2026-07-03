using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ILogReadRepository"/>. Joins to
/// <c>resources</c>/<c>instrumentation_scopes</c>, applies the tenant + severity/time
/// filters in SQL, and shapes rows into <see cref="LogRecordModel"/> exactly as the
/// former EF repository did.
/// </summary>
public abstract class LogReadRepositoryBase : DapperReadRepository, ILogReadRepository
{
    protected LogReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    private const string BaseSelect = """
        SELECT
            lr.time_unix_nano            AS TimeUnixNano,
            lr.observed_time_unix_nano   AS ObservedTimeUnixNano,
            lr.severity_number           AS SeverityNumber,
            lr.severity_text             AS SeverityText,
            lr.body_type                 AS BodyType,
            lr.body_value                AS BodyValue,
            lr.dropped_attributes_count  AS DroppedAttributesCount,
            lr.flags                     AS Flags,
            lr.trace_id                  AS TraceId,
            lr.span_id                   AS SpanId,
            lr.attributes_json           AS AttributesJson,
            r.schema_url                 AS ResourceSchemaUrl,
            r.attributes_json            AS ResourceAttributesJson,
            sc.name                      AS ScopeName,
            sc.version                   AS ScopeVersion,
            sc.schema_url                AS ScopeSchemaUrl,
            sc.attributes_json           AS ScopeAttributesJson
        FROM log_records lr
        JOIN resources r               ON lr.resource_id = r.id
        JOIN instrumentation_scopes sc ON lr.scope_id = sc.id
        WHERE r.tenant_id = @tenantId
        """;

    public async Task<LogRecordModel?> GetLogRecordByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.id = @id",
            new { tenantId = TenantId, id }, cancellationToken: cancellationToken));
        return row == null ? null : Map(row);
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.trace_id = @traceId ORDER BY lr.time_unix_nano DESC",
            new { tenantId = TenantId, traceId = traceIdHex }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.time_unix_nano >= @start AND lr.time_unix_nano <= @end ORDER BY lr.time_unix_nano DESC",
            new { tenantId = TenantId, start = startNano, end = endNano }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT DISTINCT r.attributes_json
            FROM log_records lr
            JOIN resources r ON lr.resource_id = r.id
            WHERE r.tenant_id = @tenantId
            """;
        if (startTime.HasValue) sql += " AND lr.time_unix_nano >= @start";
        if (endTime.HasValue)   sql += " AND lr.time_unix_nano <= @end";

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, new
        {
            tenantId = TenantId,
            start = startTime.HasValue ? TimeConversion.DateTimeToUnixNano(startTime.Value) : (long?)null,
            end   = endTime.HasValue   ? TimeConversion.DateTimeToUnixNano(endTime.Value)   : (long?)null
        }, cancellationToken: cancellationToken));

        return rows
            .Select(json => ExtractServiceName(DeserializeAttributes(json)))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsBySeverityAsync(int minSeverity, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        var sql = BaseSelect + " AND lr.severity_number >= @minSeverity";
        if (startTime.HasValue) sql += " AND lr.time_unix_nano >= @start";
        if (endTime.HasValue) sql += " AND lr.time_unix_nano <= @end";
        sql += " ORDER BY lr.time_unix_nano";

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(sql, new
        {
            tenantId = TenantId,
            minSeverity,
            start = startTime.HasValue ? TimeConversion.DateTimeToUnixNano(startTime.Value) : (long?)null,
            end = endTime.HasValue ? TimeConversion.DateTimeToUnixNano(endTime.Value) : (long?)null
        }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    private static LogRecordModel Map(LogRow r) => new()
    {
        Resource = new ResourceModel
        {
            SchemaUrl = r.ResourceSchemaUrl,
            Attributes = DeserializeAttributes(r.ResourceAttributesJson) ?? new Dictionary<string, object>()
        },
        InstrumentationScope = new InstrumentationScopeModel
        {
            Name = r.ScopeName,
            Version = r.ScopeVersion,
            SchemaUrl = r.ScopeSchemaUrl,
            Attributes = DeserializeAttributes(r.ScopeAttributesJson) ?? new Dictionary<string, object>()
        },
        SeverityText = r.SeverityText,
        SeverityNumber = r.SeverityNumber,
        Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>(),
        TraceIdHex = r.TraceId,
        BodyType = r.BodyType == null ? null : Enum.Parse<AttributeType>(r.BodyType),
        BodyValue = r.BodyValue,
        DroppedAttributesCount = r.DroppedAttributesCount,
        Flags = r.Flags,
        ObservedTimeUnixNano = r.ObservedTimeUnixNano,
        SpanIdHex = r.SpanId,
        TimeUnixNano = r.TimeUnixNano
    };

    private sealed class LogRow
    {
        public long? TimeUnixNano { get; set; }
        public long? ObservedTimeUnixNano { get; set; }
        public int? SeverityNumber { get; set; }
        public string? SeverityText { get; set; }
        public string? BodyType { get; set; }
        public string? BodyValue { get; set; }
        public int DroppedAttributesCount { get; set; }
        public int Flags { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public string? AttributesJson { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ResourceAttributesJson { get; set; }
        public string ScopeName { get; set; } = null!;
        public string? ScopeVersion { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public string? ScopeAttributesJson { get; set; }
    }
}
