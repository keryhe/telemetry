-- Test-only seed: a SUMMARY metric with two series (route=/a, /b) for UI verification.
-- OTLP Summary is legacy and the .NET SDK cannot emit it, so the TestDataGenerator can't produce one;
-- this seeds the read path directly. NOT part of the committed schema — run manually against a dev DB:
--
--   psql -d telemetry -f src/Keryhe.Telemetry.TestDataGenerator/seed-summary-demo.sql
--
-- Assumes tenant_id = 1 (the default). Adjust v_tenant if your dev data uses another tenant.
-- Re-runnable: resource/scope upsert on their unique hashes; metric+points are re-inserted each run.

DO $$
DECLARE
    v_tenant BIGINT := 1;
    v_res    BIGINT;
    v_scope  BIGINT;
    v_metric BIGINT;
    v_now    BIGINT := (EXTRACT(EPOCH FROM NOW()) * 1e9)::BIGINT;
    v_min    BIGINT := 60000000000;   -- 60s in nanoseconds
BEGIN
    INSERT INTO resources (tenant_id, resource_hash, attributes_json)
    VALUES (v_tenant, rpad('seed_summary_resource', 64, '0'), '{"service.name":"summary-demo"}')
    ON CONFLICT (tenant_id, resource_hash) DO UPDATE SET attributes_json = EXCLUDED.attributes_json
    RETURNING id INTO v_res;

    INSERT INTO instrumentation_scopes (name, version, scope_hash, attributes_json)
    VALUES ('seed', '1.0', rpad('seed_summary_scope', 64, '0'), '{}')
    ON CONFLICT (scope_hash) DO UPDATE SET name = EXCLUDED.name
    RETURNING id INTO v_scope;

    INSERT INTO metrics (resource_id, scope_id, name, description, unit, type)
    VALUES (v_res, v_scope, 'demo.request.summary_ms', 'Seeded summary for UI verification', 'ms', 'SUMMARY')
    RETURNING id INTO v_metric;

    -- 30 cumulative points per series (60s apart, last 30 min), increasing count/sum; two label sets.
    INSERT INTO summary_data_points
        (metric_id, start_time_unix_nano, time_unix_nano, count, sum_value, quantile_values, attributes_json)
    SELECT
        v_metric,
        v_now - 30 * v_min,
        v_now - (30 - g) * v_min,
        (g + 1) * 100 * mult,
        (g + 1) * 12000.0 * mult,
        format('[{"Quantile":0.5,"Value":%s},{"Quantile":0.95,"Value":%s},{"Quantile":0.99,"Value":%s}]',
               110 + g, 300 + 2 * g, 480 + 3 * g)::jsonb,
        attrs
    FROM generate_series(0, 29) AS g,
         (VALUES ('{"route":"/a"}'::jsonb, 1), ('{"route":"/b"}'::jsonb, 2)) AS lab(attrs, mult);
END $$;
