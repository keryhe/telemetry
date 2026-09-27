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

public class ClickHouseTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
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

    // Decision 3/Phase 3 pin helper: ClickHouse's created_at default is evaluated at statement
    // execution, so no 5-second back-off is needed here -- see ClickHouseLogReadRepository's
    // identical override.
    protected override string DatabaseClockNowExpr => "now64(9)";

    // ClickHouse can't reliably correlate a subquery to the outer row, so "any span in this trace
    // matches" is expressed as an uncorrelated membership test instead of EXISTS (decision 9,
    // list-pages-server-side plan Phase 1). The subquery's own spans alias is still needed so
    // innerPredicate/innerTimeClause (built against that alias by the caller) resolve.
    protected override string SpanLevelMatchPredicate(string traceIdColumn, string innerTimeClause, string innerPredicate, string spanAlias = "s2")
        => $"{traceIdColumn} IN (SELECT {spanAlias}.trace_id FROM spans {spanAlias} WHERE 1=1{innerTimeClause} AND {innerPredicate})";

    // Same uncorrelated shape, joined to the matching span's own resource for a resource-attribute
    // search term (list-pages-server-side plan, Phase 3).
    protected override string SpanLevelMatchPredicateWithResource(string traceIdColumn, string innerTimeClause, string innerPredicate)
        => $"{traceIdColumn} IN (SELECT s2.trace_id FROM spans s2 JOIN resources rs2 ON rs2.id = s2.resource_id WHERE 1=1{innerTimeClause} AND {innerPredicate})";

    protected override string TraceIdInPredicate(string alias) => $"{alias}.trace_id IN @traceIds";
    protected override string ResourceIdInPredicate => "id IN @resourceIds";

    // Decision 34: spans is a ReplacingMergeTree, so a page's anchor rows are deduped with
    // LIMIT 1 BY trace_id, span_id; orphan_roots is read through FINAL. Decision 9's uncorrelated
    // NOT EXISTS equivalent for the late-root re-check.
    protected override string AnchorsSql => """
        (
            SELECT trace_id, id AS anchor_span_pk, span_id AS anchor_span_id, resource_id, name AS root_name, kind AS anchor_kind,
                   start_time_unix_nano AS anchor_start, end_time_unix_nano AS anchor_end, created_at AS anchor_created_at
            FROM (
                SELECT trace_id, id, span_id, resource_id, name, kind, parent_span_id, start_time_unix_nano, end_time_unix_nano, created_at
                FROM spans
                LIMIT 1 BY trace_id, span_id
            )
            WHERE parent_span_id IS NULL
            UNION ALL
            SELECT o.trace_id, sp.id, sp.span_id, o.resource_id, sp.name, sp.kind, o.start_time_unix_nano, o.end_time_unix_nano, o.detected_at
            FROM (SELECT * FROM orphan_roots FINAL) o
            JOIN (
                SELECT trace_id, span_id, id, name, kind FROM spans LIMIT 1 BY trace_id, span_id
            ) sp ON sp.trace_id = o.trace_id AND sp.span_id = o.span_id
            WHERE o.trace_id NOT IN (
                SELECT trace_id FROM (SELECT trace_id, parent_span_id FROM spans LIMIT 1 BY trace_id, span_id)
                WHERE parent_span_id IS NULL
            )
        )
        """;
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

    // Real bug found while wiring Phase 4's bucketed series queries (same shape as the
    // DatabaseClockNowExpr gap Phase 3 found on SqlServer/MySql/ClickHouse's trace repositories):
    // this class had no BucketIndexExpr override, so the new bucket-index SQL every series loader
    // builds would have fallen back to the Postgres-only `numerator / denominator`, which ClickHouse
    // promotes to Float64 instead of truncating — see DapperReadRepository's own doc comment.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"intDiv({numerator}, {denominator})";

    // Real bug found via the Phase 4 integration tests: ClickHouse's COALESCE requires one common
    // supertype across its branches and refuses to promote Int64 (value_int) to Float64
    // (value_double) implicitly — "NO_COMMON_TYPE". Cast the integer branch explicitly first; see
    // MetricReadRepositoryBase.CoalesceValueExpr's own doc comment.
    protected override string CoalesceValueExpr() => "COALESCE(dp.value_double, CAST(dp.value_int AS Nullable(Float64)))";

    // Decision 22/Phase 4: ClickHouse's "last point per stream-bucket" query uses argMax per
    // column instead of ROW_NUMBER() OVER (...) — see MetricReadRepositoryBase's own doc comment
    // on BuildLastPerStreamBucketSql for why.
    protected override string BuildLastPerStreamBucketSql(string table, string idInList, string timeClause,
        string labelClause, string bucketExpr, IReadOnlyList<string> valueColumns)
    {
        var cols = string.Join(", ", valueColumns.Select(c => $"argMax(dp.{c}, dp.time_unix_nano) AS {c}"));
        return $"""
            SELECT dp.metric_id AS metric_id, dp.attributes_json AS attributes_json, {bucketExpr} AS bucket,
                   max(dp.time_unix_nano) AS time_unix_nano, {cols}
            FROM {table} dp
            WHERE dp.metric_id IN ({idInList}){timeClause}{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json, {bucketExpr}
            """;
    }

    protected override string BuildLastPerStreamSql(string table, string idInList, string timeClause,
        string labelClause, IReadOnlyList<string> valueColumns)
    {
        var cols = string.Join(", ", valueColumns.Select(c => $"argMax(dp.{c}, dp.time_unix_nano) AS {c}"));
        return $"""
            SELECT dp.metric_id AS metric_id, dp.attributes_json AS attributes_json,
                   max(dp.time_unix_nano) AS time_unix_nano, {cols}
            FROM {table} dp
            WHERE dp.metric_id IN ({idInList}){timeClause}{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json
            """;
    }

    // Analytics-tier exemplar keyset paging (decision 26) is inherited unchanged from
    // MetricReadRepositoryBase.GetMetricExemplarsAsync — see that method's own doc comment for why
    // the base implementation (not the standard-tier newest-500 scan) is what every analytics
    // provider uses, and for the documented simplification versus the plan's literal
    // per-exemplar-ordinal SQL unnesting.
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

    // =========================================================================
    // TRACE ROLLUPS (list-pages-server-side plan, Phase 3, decisions 37-38, 41)
    // =========================================================================

    /// <summary>
    /// Orphan detection, ClickHouse dialect: an uncorrelated <c>NOT IN</c> in place of the
    /// relational providers' correlated <c>NOT EXISTS</c> (ClickHouse can't reliably correlate a
    /// subquery to the outer row — decision 9's same reasoning), and spans deduped with
    /// <c>LIMIT 1 BY trace_id, span_id</c> (decision 34) since <c>spans</c> is a
    /// <c>ReplacingMergeTree</c> that may still hold un-merged duplicates. A lightweight
    /// <c>DELETE FROM</c> (the same mutation this codebase's retention sweeps already use against
    /// <c>spans</c>) replaces the range before re-inserting, mirroring the relational providers'
    /// delete-then-insert shape even though <c>orphan_roots</c> is itself a
    /// <c>ReplacingMergeTree</c> — a plain re-insert with a fresh <c>detected_at</c> would leave a
    /// trace whose real root has since arrived sitting in the table forever.
    /// </summary>
    public async Task RollOrphanRootsAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM orphan_roots WHERE start_time_unix_nano >= @from AND start_time_unix_nano < @to",
            new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));

        const string insertSql = """
            INSERT INTO orphan_roots (trace_id, span_id, resource_id, start_time_unix_nano, end_time_unix_nano)
            SELECT e2.trace_id, e2.span_id, e2.resource_id, e2.start_time_unix_nano, e2.end_time_unix_nano
            FROM (
                SELECT ea.trace_id, ea.span_id
                FROM (
                    SELECT e.trace_id AS trace_id, MIN(e.span_id) AS span_id
                    FROM (
                        SELECT trace_id, span_id, start_time_unix_nano
                        FROM spans
                        LIMIT 1 BY trace_id, span_id
                    ) e
                    JOIN (
                        SELECT trace_id,
                               MIN(start_time_unix_nano) AS min_start,
                               SUM(CASE WHEN parent_span_id IS NULL THEN 1 ELSE 0 END) AS null_parent_count
                        FROM (
                            SELECT trace_id, parent_span_id, start_time_unix_nano
                            FROM spans
                            LIMIT 1 BY trace_id, span_id
                        )
                        WHERE trace_id IN (
                            SELECT DISTINCT trace_id FROM spans
                            WHERE start_time_unix_nano >= @from AND start_time_unix_nano < @to
                        )
                        GROUP BY trace_id
                    ) c ON c.trace_id = e.trace_id AND c.min_start = e.start_time_unix_nano
                    WHERE c.null_parent_count = 0
                    GROUP BY e.trace_id
                ) ea
            ) earliest
            JOIN (
                SELECT trace_id, span_id, parent_span_id, resource_id, start_time_unix_nano, end_time_unix_nano
                FROM spans
                LIMIT 1 BY trace_id, span_id
            ) e2 ON e2.trace_id = earliest.trace_id AND e2.span_id = earliest.span_id
            WHERE e2.parent_span_id NOT IN (SELECT span_id FROM spans)
               OR e2.parent_span_id IS NULL
            """;
        await conn.ExecuteAsync(new CommandDefinition(insertSql, new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));
    }

    /// <summary>
    /// Trace summary recompute, ClickHouse dialect — same anchors-plus-full-trace-aggregation
    /// shape as <see cref="LogRollupRepositoryBase.RollTraceMinutesAsync"/>, using nested
    /// subqueries instead of a <c>WITH</c> clause (sidesteps any doubt about ClickHouse's
    /// <c>WITH ... INSERT</c> clause placement) and deduping every <c>spans</c> read with
    /// <c>LIMIT 1 BY trace_id, span_id</c> (decision 34).
    /// </summary>
    public async Task RollTraceMinutesAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        const long nanosPerMinute = 60_000_000_000L;
        var lbColumns = string.Join(", ", LatencyBucketSql.ColumnNames());
        var lbSelect = LatencyBucketSql.SumCaseColumns("f.duration_nano");

        var anchorsSql = $"""
            (
                SELECT trace_id, resource_id, name AS root_name, kind AS anchor_kind, start_time_unix_nano AS anchor_start
                FROM (
                    SELECT trace_id, resource_id, name, kind, parent_span_id, start_time_unix_nano
                    FROM spans
                    LIMIT 1 BY trace_id, span_id
                )
                WHERE parent_span_id IS NULL AND start_time_unix_nano >= @from AND start_time_unix_nano < @to
                UNION ALL
                SELECT o.trace_id, o.resource_id, sp.name, sp.kind, o.start_time_unix_nano
                FROM orphan_roots AS o FINAL
                JOIN (
                    SELECT trace_id, span_id, name, kind FROM spans LIMIT 1 BY trace_id, span_id
                ) sp ON sp.trace_id = o.trace_id AND sp.span_id = o.span_id
                WHERE o.start_time_unix_nano >= @from AND o.start_time_unix_nano < @to
                  AND o.trace_id NOT IN (
                      SELECT trace_id FROM (
                          SELECT trace_id, parent_span_id FROM spans LIMIT 1 BY trace_id, span_id
                      )
                      WHERE parent_span_id IS NULL
                  )
            )
            """;

        var sql = $"""
            INSERT INTO trace_rollup_minute (bucket_unix_nano, resource_id, root_name, inbound, trace_count, error_count, duration_sum_ms, duration_max_ms, {lbColumns})
            SELECT f.bucket, f.resource_id, f.root_name, f.inbound,
                   COUNT(*), SUM(f.has_error),
                   SUM(f.duration_nano) / 1000000.0, MAX(f.duration_nano) / 1000000.0,
                   {lbSelect}
            FROM (
                SELECT tl.bucket AS bucket, tl.resource_id AS resource_id,
                       if(rn.rn <= 200, tl.root_name, '__other__') AS root_name,
                       tl.inbound AS inbound, tl.has_error AS has_error, tl.duration_nano AS duration_nano
                FROM (
                    SELECT a.trace_id AS trace_id, a.resource_id AS resource_id, a.root_name AS root_name,
                           if(a.anchor_kind IN ('SERVER', 'CONSUMER'), 1, 0) AS inbound,
                           (intDiv(a.anchor_start, {nanosPerMinute}) * {nanosPerMinute}) AS bucket,
                           MAX(if(fs.status_code = 'ERROR', 1, 0)) AS has_error,
                           (MAX(fs.end_time_unix_nano) - MIN(fs.start_time_unix_nano)) AS duration_nano
                    FROM {anchorsSql} a
                    JOIN (
                        SELECT trace_id, status_code, start_time_unix_nano, end_time_unix_nano
                        FROM spans
                        LIMIT 1 BY trace_id, span_id
                    ) fs ON fs.trace_id = a.trace_id
                    GROUP BY a.trace_id, a.resource_id, a.root_name, a.anchor_kind, a.anchor_start
                ) tl
                JOIN (
                    SELECT bucket, resource_id, root_name,
                           row_number() OVER (PARTITION BY bucket, resource_id ORDER BY cnt DESC, root_name) AS rn
                    FROM (
                        SELECT bucket, resource_id, root_name, COUNT(*) AS cnt
                        FROM (
                            SELECT a.trace_id AS trace_id, a.resource_id AS resource_id, a.root_name AS root_name,
                                   (intDiv(a.anchor_start, {nanosPerMinute}) * {nanosPerMinute}) AS bucket
                            FROM {anchorsSql} a
                        ) t2
                        GROUP BY bucket, resource_id, root_name
                    ) nc
                ) rn ON rn.bucket = tl.bucket AND rn.resource_id = tl.resource_id AND rn.root_name = tl.root_name
            ) f
            GROUP BY f.bucket, f.resource_id, f.root_name, f.inbound
            """;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM trace_rollup_minute WHERE bucket_unix_nano >= @from AND bucket_unix_nano < @to",
            new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(sql, new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));
    }

    public async Task RollTraceHoursAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        const long nanosPerHour = 3_600_000_000_000L;
        var lbColumns = string.Join(", ", LatencyBucketSql.ColumnNames());
        var lbSums = string.Join(", ", LatencyBucketSql.ColumnNames().Select(c => $"SUM(trm.{c})"));

        var sql = $"""
            INSERT INTO trace_rollup_hour (bucket_unix_nano, resource_id, root_name, inbound, trace_count, error_count, duration_sum_ms, duration_max_ms, {lbColumns})
            SELECT intDiv(trm.bucket_unix_nano, {nanosPerHour}) * {nanosPerHour}, trm.resource_id, trm.root_name, trm.inbound,
                   SUM(trm.trace_count), SUM(trm.error_count),
                   SUM(trm.duration_sum_ms), MAX(trm.duration_max_ms),
                   {lbSums}
            FROM trace_rollup_minute AS trm FINAL
            WHERE trm.bucket_unix_nano >= @from AND trm.bucket_unix_nano < @to
            GROUP BY intDiv(trm.bucket_unix_nano, {nanosPerHour}) * {nanosPerHour}, trm.resource_id, trm.root_name, trm.inbound
            """;
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { from = fromInclusive, to = toExclusive }, cancellationToken: ct));
    }
}
