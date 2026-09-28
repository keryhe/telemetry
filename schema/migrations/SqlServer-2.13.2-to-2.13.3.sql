-- Migration: schema 2.13.2 -> 2.13.3 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -i SqlServer-2.13.2-to-2.13.3.sql
--
-- Every step is idempotent, so re-running this on a database already at 2.13.3 does nothing but
-- refresh the schema_version timestamp.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 7 section, decision 8/39):
--
--   resources.service_name: a real column, backfilled below from attributes_json's
--   "service.name" key via JSON_VALUE (the same extraction ResourceServiceNameExpr used to
--   compile per-query). ResourceServiceNameExpr() is now just "{alias}.service_name" on every
--   provider, including this one -- the service_name column itself is NOT part of the decision
--   39 tiering split. SQL Server stays on the standard tier for search: no index changes to the
--   phase 1 unindexed LIKE/JSON_VALUE predicates, still bounded by the 24-hour raw search window
--   (ProviderCapabilities). No SQL Server full-text catalog is added -- out of scope per tiering.

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('resources') AND name = 'service_name')
BEGIN
    ALTER TABLE resources ADD service_name NVARCHAR(255) NULL;
END
GO

UPDATE resources
SET service_name = JSON_VALUE(attributes_json, '$."service.name"')
WHERE service_name IS NULL AND attributes_json IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_resources_service_name' AND object_id = OBJECT_ID('resources'))
BEGIN
    CREATE INDEX idx_resources_service_name ON resources (service_name);
END
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS target
USING (VALUES (N'2.13.3')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO
