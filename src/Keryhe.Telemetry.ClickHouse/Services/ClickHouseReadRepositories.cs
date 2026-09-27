using System.Data.Common;
using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.ClickHouse.Services;

// =============================================================================
// ClickHouse read repositories. The shared Dapper bases hold the dialect-neutral read SQL
// and shape rows in C# (attributes are deserialized from JSON text, so no Postgres `->>` on
// the hot path). ClickHouse overrides only where its dialect differs: JSON extraction and the
// alert-rule CRUD (no identity columns / transactional UPDATE — see below). Connections come
// from ConnectionStrings:Api.
// =============================================================================

internal static class ClickHouseConnectionFactory
{
    public static async Task<DbConnection> OpenReadAsync(string connectionString, CancellationToken ct)
    {
        var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>
/// Shared body for the <c>AttributePredicate</c> dialect hook (list-pages-server-side plan,
/// Phase 1), duplicated as an override on every ClickHouse read repository class below (there is
/// no mixin) but sharing one implementation. <c>JSONExtractRaw</c>, not <c>JSONExtractString</c>,
/// is deliberate: <c>JSONExtractString</c> returns <c>''</c> for a non-string (number/boolean)
/// value, which would silently break a filter like <c>http.status_code:500</c> (decision 7).
/// Trimming the surrounding quotes in SQL normalizes a JSON string's raw form (<c>"500"</c>) to
/// the same text as a JSON number's raw form (<c>500</c>), so both match the same bound value.
/// <c>AttributeKeyParamValue</c> needs no override: ClickHouse's key parameter is the raw key,
/// same as the base default.
/// </summary>
internal static class ClickHouseJsonAttributeHooks
{
    public static string Predicate(string column, string keyParam, string valueParam, bool negated)
    {
        var col = $"coalesce({column}, '')";
        var expr = $"lowerUTF8(trim(BOTH '\"' FROM JSONExtractRaw({col}, {keyParam})))";
        var valueExpr = $"lowerUTF8({valueParam})";
        return negated
            ? $"(JSONHas({col}, {keyParam}) = 0 OR {expr} != {valueExpr})"
            : $"JSONHas({col}, {keyParam}) = 1 AND {expr} = {valueExpr}";
    }
}

public class ClickHouseTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext, TraceQueryCache traceQueryCache)
    : TraceReadRepositoryBase(tenantContext, traceQueryCache)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // Same dialect hooks as ClickHouseLogReadRepository, needed here too now that the service/tag
    // filters run in SQL (list-page-scale plan, Phase 4). JSONHas tests key presence regardless
    // of the value's type, unlike JSONExtractString which returns '' for a non-scalar value.
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"JSONExtractString(coalesce({resourceAlias}.attributes_json, ''), 'service.name')";
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"JSONHas(coalesce({jsonColumn}, ''), {keyParam}) = 1";

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => ClickHouseJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // ClickHouse can't reliably correlate a subquery to the outer row, so "any span in this trace
    // matches" is expressed as an uncorrelated membership test instead of EXISTS (decision 9,
    // list-pages-server-side plan Phase 1). The subquery's own spans alias is still needed so
    // innerPredicate/innerTimeClause (built against that alias by the caller) resolve.
    protected override string SpanLevelMatchPredicate(string traceIdColumn, string innerTimeClause, string innerPredicate, string spanAlias = "s2")
        => $"{traceIdColumn} IN (SELECT {spanAlias}.trace_id FROM spans {spanAlias} WHERE 1=1{innerTimeClause} AND {innerPredicate})";
}

public class ClickHouseMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // Load-bearing for the metric label-filter fix (list-pages-server-side plan, Phase 1):
    // MetricReadRepositoryBase's data-point getters call AttributePredicate polymorphically, so
    // ClickHouse needs its own override here too, not just on the trace/log repos.
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => ClickHouseJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class ClickHouseLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => ClickHouseJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // attributes_json is a Nullable(String) holding JSON text; extract service.name with
    // JSONExtractString (coalesce guards NULL rows). ILIKE, LIMIT/OFFSET paging, and backslash
    // LIKE-escaping all match the Postgres defaults, so those hooks are inherited unchanged.
    protected override string ResourceServiceNameExpr(string resourceAlias = "r") => $"JSONExtractString(coalesce({resourceAlias}.attributes_json, ''), 'service.name')";

    // ClickHouse's `/` on Int64 operands promotes to Float64; intDiv keeps histogram
    // bucket-index math as true integer floor division.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"intDiv({numerator}, {denominator})";

    // Decision 3/Phase 2 pin helper: ClickHouse's created_at default is evaluated at statement
    // execution, so no 5-second back-off is needed here.
    protected override string DatabaseClockNowExpr => "now64(9)";

    // Rollup tables are ReplacingMergeTree(rolled_at); FINAL collapses a minute rolled twice to
    // its newest row without waiting for a background merge (decisions 37-38).
    protected override string RollupFinalHint => " FINAL";
}

public class ClickHouseResourceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);
}

public class ClickHouseTenantCatalogRepository(IConfiguration configuration)
    : TenantCatalogRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);
}

/// <summary>
/// ClickHouse alert-rule repository. The inherited SELECT-based reads work unchanged, but the
/// write path is overridden because ClickHouse has no identity columns, no <c>RETURNING</c>, and
/// no transactional <c>UPDATE</c>: ids are generated in-process, edits use <c>ALTER TABLE ...
/// UPDATE</c> mutations, and <c>TryClaimFireAsync</c> is best-effort (read-check-then-update is
/// not atomic, so under concurrent evaluators a rule could double-fire). Acceptable for the
/// control plane — no host currently drives alert evaluation on a schedule.
/// </summary>
public class ClickHouseAlertRuleRepository(IConfiguration configuration, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    // In-process id generators (ClickHouse has no auto-increment). Seeded from the clock so ids
    // stay unique-enough across restarts for these low-volume control-plane tables.
    private static int _ruleSeq = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x3FFFFFFF);
    private static int NextRuleId() => Interlocked.Increment(ref _ruleSeq);

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // JSON columns are plain String in ClickHouse — no cast. ReturningIdentity/ClaimFireSql are
    // abstract in the base but unused here (Create/TryClaim are fully overridden below).
    protected override string JsonParam(string parameterName) => $"@{parameterName}";
    protected override string ReturningIdentity => string.Empty;
    protected override string ClaimFireSql => string.Empty;

    public override async Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = TenantId;
        var createdAt = DateTime.UtcNow;
        var id = NextRuleId();

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO alert_rules
                (id, tenant_id, name, type, service_name, condition_json, webhook_url, cooldown_minutes, enabled, created_at)
            VALUES
                (@id, @tenantId, @name, @type, @serviceName, @conditionJson, @webhookUrl, @cooldownMinutes, @enabled, @createdAt)
            """, new
        {
            id,
            tenantId,
            name = rule.Name,
            type = rule.Type.ToString(),
            serviceName = rule.ServiceName,
            conditionJson = rule.ConditionJson,
            webhookUrl = rule.WebhookUrl,
            cooldownMinutes = rule.CooldownMinutes,
            enabled = (byte)(rule.Enabled ? 1 : 0),
            createdAt
        }, cancellationToken: ct));

        return new AlertRule
        {
            Id = id,
            TenantId = tenantId,
            Name = rule.Name,
            Type = rule.Type,
            ServiceName = rule.ServiceName,
            ConditionJson = rule.ConditionJson,
            WebhookUrl = rule.WebhookUrl,
            CooldownMinutes = rule.CooldownMinutes,
            Enabled = rule.Enabled,
            CreatedAt = createdAt,
            LastFiredAt = null
        };
    }

    public override async Task<AlertRule> UpdateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        var tenantId = TenantId;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            ALTER TABLE alert_rules UPDATE
                name = @name, type = @type, service_name = @serviceName, condition_json = @conditionJson,
                webhook_url = @webhookUrl, cooldown_minutes = @cooldownMinutes, enabled = @enabled
            WHERE id = @id AND tenant_id = @tenantId
            """, new
        {
            id = rule.Id,
            tenantId,
            name = rule.Name,
            type = rule.Type.ToString(),
            serviceName = rule.ServiceName,
            conditionJson = rule.ConditionJson,
            webhookUrl = rule.WebhookUrl,
            cooldownMinutes = rule.CooldownMinutes,
            enabled = (byte)(rule.Enabled ? 1 : 0)
        }, cancellationToken: ct));

        // The mutation is applied asynchronously; return the caller's intended state rather than
        // re-reading (which could still observe the pre-mutation row).
        return new AlertRule
        {
            Id = rule.Id,
            TenantId = tenantId,
            Name = rule.Name,
            Type = rule.Type,
            ServiceName = rule.ServiceName,
            ConditionJson = rule.ConditionJson,
            WebhookUrl = rule.WebhookUrl,
            CooldownMinutes = rule.CooldownMinutes,
            Enabled = rule.Enabled,
            CreatedAt = rule.CreatedAt,
            LastFiredAt = rule.LastFiredAt
        };
    }

    public override async Task DeleteRuleAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM alert_rules WHERE id = @id AND tenant_id = @tenantId",
            new { id, tenantId = TenantId }, cancellationToken: ct));
    }

    public override async Task<bool> TryClaimFireAsync(int ruleId, long tenantId, int cooldownMinutes, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Best-effort, NON-ATOMIC cooldown claim: read the last fire time, decide in C#, then
        // mutate. ClickHouse offers no atomic conditional UPDATE, so a concurrent evaluator could
        // also pass this check and double-fire. Acceptable given no scheduled evaluation runs.
        var lastFired = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT last_fired_at FROM alert_rules WHERE id = @ruleId AND tenant_id = @tenantId AND enabled = 1 LIMIT 1",
            new { ruleId, tenantId }, cancellationToken: ct));

        if (lastFired is not null && lastFired.Value > DateTime.UtcNow.AddMinutes(-cooldownMinutes))
            return false;

        await conn.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE alert_rules UPDATE last_fired_at = now64(9) WHERE id = @ruleId AND tenant_id = @tenantId",
            new { ruleId, tenantId }, cancellationToken: ct));
        return true;
    }

    public override async Task AddAlertEventAsync(AlertEvent alertEvent, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO alert_events (id, rule_id, fired_at, details_json)
            VALUES (@id, @ruleId, @firedAt, @detailsJson)
            """, new
        {
            id = RowId.Next(),
            ruleId = alertEvent.RuleId,
            firedAt = alertEvent.FiredAt,
            detailsJson = alertEvent.DetailsJson
        }, cancellationToken: ct));
    }
}

/// <summary>
/// ClickHouse implementation of the <see cref="IRetentionSettingsRepository"/> sweeps. Two
/// things differ from the relational providers, matching the same "control-plane is
/// best-effort" pattern already used for alert-rule CRUD:
///
/// <see cref="UpdateSettingsAsync"/> overrides the base's plain <c>UPDATE</c> with an
/// <c>ALTER TABLE ... UPDATE</c> mutation, since ClickHouse has no in-place row update.
///
/// There are no foreign keys and so no cascades, so any child rows must be deleted explicitly.
/// The trace sweep has none left to delete: since schema 2.11.0 a span's events and links are JSON
/// columns on the span row, so deleting the span takes them with it.
/// A lightweight <c>DELETE</c> is an asynchronous mutation that reports no row count, so every
/// sweep pre-counts what it is about to remove. That count is the return value; it is taken before
/// the mutation is issued and is therefore a snapshot, not a receipt.
/// </summary>
public class ClickHouseRetentionSettingsRepository(IConfiguration configuration)
    : RetentionSettingsRepositoryBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    public override async Task UpdateSettingsAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        var updatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            ALTER TABLE retention_settings UPDATE
                trace_retention_days = @traceRetentionDays,
                log_retention_days = @logRetentionDays,
                metric_retention_days = @metricRetentionDays,
                updated_at = @updatedAt
            WHERE id = 1
            """,
            new
            {
                traceRetentionDays = settings.TraceRetentionDays,
                logRetentionDays = settings.LogRetentionDays,
                metricRetentionDays = settings.MetricRetentionDays,
                updatedAt
            },
            cancellationToken: ct));

        settings.UpdatedAt = updatedAt;
    }

    public override async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var args = new { cutoff = CutoffNano(retentionPeriod) };

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM spans WHERE start_time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spans WHERE start_time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        return count;
    }

    public override async Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var args = new { cutoff = CutoffNano(retentionPeriod) };

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var count = 0;
        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
        {
            count += await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT count() FROM {table} WHERE time_unix_nano < @cutoff",
                args, cancellationToken: cancellationToken));
        }

        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE time_unix_nano < @cutoff",
                args, cancellationToken: cancellationToken));
        }

        return count;
    }

    public override async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var args = new { cutoff = CutoffNano(retentionPeriod) };

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM log_records WHERE time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_rollup_minute WHERE bucket_unix_nano < @cutoff", args, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_rollup_hour WHERE bucket_unix_nano < @cutoff", args, cancellationToken: cancellationToken));

        return count;
    }
}

/// <summary>
/// ClickHouse implementation of <see cref="IRollupRepository"/>. Does not extend
/// <see cref="LogRollupRepositoryBase"/> — that base's delete-then-insert-in-one-transaction shape
/// doesn't fit ClickHouse (no transactions, mutations are asynchronous). Instead:
/// <list type="bullet">
/// <item>The lease claim is best-effort, read-check-then-update, the same "control-plane is
/// best-effort" pattern as <c>ClickHouseAlertRuleRepository.TryClaimFireAsync</c> — acceptable
/// because a double-rolled minute is harmless (see the next point), unlike a double-fired alert.</item>
/// <item>A roll is a plain INSERT with a fresh <c>rolled_at</c>, never a DELETE: log_rollup_minute/
/// _hour are <c>ReplacingMergeTree(rolled_at)</c>, so a minute rolled twice by two racing API
/// instances collapses to the newest row at merge time, and every read goes through FINAL
/// (<see cref="ClickHouseLogReadRepository.RollupFinalHint"/>) so it never depends on that merge
/// having already happened.</item>
/// </list>
/// </summary>
public class ClickHouseLogRollupRepository(IConfiguration configuration) : IRollupRepository
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    private Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, ct);

    public async Task<bool> TryClaimLeaseAsync(string signal, string granularity, string owner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var leaseExpiresAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT lease_expires_at FROM rollup_state WHERE signal_name = @signal AND granularity = @granularity LIMIT 1",
            new { signal, granularity }, cancellationToken: ct));

        if (leaseExpiresAt is { } expires && expires >= DateTime.UtcNow)
            return false;

        await conn.ExecuteAsync(new CommandDefinition(
            """
            ALTER TABLE rollup_state UPDATE lease_owner = @owner, lease_expires_at = @newExpiry
            WHERE signal_name = @signal AND granularity = @granularity
            """,
            new { owner, newExpiry = DateTime.UtcNow + leaseDuration, signal, granularity }, cancellationToken: ct));
        return true;
    }

    public async Task<RollupStateInfo?> GetStateAsync(string signal, string granularity, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RollupStateInfo>(new CommandDefinition(
            """
            SELECT coverage_start_unix_nano AS CoverageStartUnixNano,
                   rolled_until_unix_nano   AS RolledUntilUnixNano,
                   repassed_until_unix_nano AS RepassedUntilUnixNano
            FROM rollup_state FINAL WHERE signal_name = @signal AND granularity = @granularity
            """,
            new { signal, granularity }, cancellationToken: ct));
    }

    public async Task SetCoverageStartAsync(string signal, string granularity, long coverageStartUnixNano, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            ALTER TABLE rollup_state UPDATE coverage_start_unix_nano = @coverageStartUnixNano
            WHERE signal_name = @signal AND granularity = @granularity AND coverage_start_unix_nano IS NULL
            """,
            new { coverageStartUnixNano, signal, granularity }, cancellationToken: ct));
    }

    public async Task AdvanceRolledUntilAsync(string signal, string granularity, long rolledUntilUnixNano, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE rollup_state UPDATE rolled_until_unix_nano = @v WHERE signal_name = @signal AND granularity = @granularity",
            new { v = rolledUntilUnixNano, signal, granularity }, cancellationToken: ct));
    }

    public async Task AdvanceRepassedUntilAsync(string signal, string granularity, long repassedUntilUnixNano, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE rollup_state UPDATE repassed_until_unix_nano = @v WHERE signal_name = @signal AND granularity = @granularity",
            new { v = repassedUntilUnixNano, signal, granularity }, cancellationToken: ct));
    }

    public async Task RollLogMinutesAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        const long nanosPerMinute = 60_000_000_000L;
        var sql = $"""
            INSERT INTO log_rollup_minute (bucket_unix_nano, resource_id, trace_count, debug_count, info_count, warn_count, error_count, fatal_count, rolled_at)
            SELECT intDiv(lr.time_unix_nano, {nanosPerMinute}) * {nanosPerMinute}, lr.resource_id,
                   {LogSeverityGroupSql.SumCaseColumns("lr.severity_number")}, now64(9)
            FROM log_records lr
            WHERE lr.time_unix_nano >= @from AND lr.time_unix_nano < @to
            GROUP BY intDiv(lr.time_unix_nano, {nanosPerMinute}) * {nanosPerMinute}, lr.resource_id
            """;
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));
    }

    public async Task RollLogHoursAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        const long nanosPerHour = 3_600_000_000_000L;
        var sql = $"""
            INSERT INTO log_rollup_hour (bucket_unix_nano, resource_id, trace_count, debug_count, info_count, warn_count, error_count, fatal_count, rolled_at)
            SELECT intDiv(lrm.bucket_unix_nano, {nanosPerHour}) * {nanosPerHour}, lrm.resource_id,
                   SUM(lrm.trace_count), SUM(lrm.debug_count), SUM(lrm.info_count),
                   SUM(lrm.warn_count), SUM(lrm.error_count), SUM(lrm.fatal_count), now64(9)
            FROM log_rollup_minute AS lrm FINAL
            WHERE lrm.bucket_unix_nano >= @from AND lrm.bucket_unix_nano < @to
            GROUP BY intDiv(lrm.bucket_unix_nano, {nanosPerHour}) * {nanosPerHour}, lrm.resource_id
            """;
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));
    }
}
