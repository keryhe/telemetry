-- Migration: schema 2.10.0 -> 2.11.0 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -b -I -i SqlServer-2.10.0-to-2.11.0.sql
--
-- -b matters: it makes sqlcmd stop on the first error instead of running the remaining batches
-- against a rolled-back transaction. Requires SQL Server 2016 or later (FOR JSON PATH,
-- DROP ... IF EXISTS).
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.11.0 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.11.0 or it is left exactly as it
-- was.
--
-- What changed, and why (see plans/span-events-links-json-hypertable.md for the full rationale):
--
--   span_events and span_links were never read or written independently of their parent span --
--   every read loads them by span_id and immediately re-attaches them in memory (the same access
--   pattern exemplars_json was introduced for in 2.9.0) -- so both collapse into two new nullable
--   columns on spans: events_json/links_json. NULL means "no events"/"no links", matching how
--   exemplars_json uses NULL rather than an empty array for the same reason.
--
--   Existing rows in span_events/span_links are folded into their parent spans row BEFORE the
--   child tables are dropped, or that data is lost. The JSON shape below matches exactly what
--   System.Text.Json produces for List<SpanEventModel>/List<SpanLinkModel> with no naming policy
--   configured -- i.e. the C# property names verbatim (PascalCase), since that is what the read
--   path (Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs) deserializes on the other
--   end. FOR JSON PATH's default null-handling (omit properties whose value is NULL) matches how
--   a missing/absent JSON property deserializes back to a null model property, so no
--   INCLUDE_NULL_VALUES is needed.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

-- =============================================================================
-- 2.11.0 -- add events_json/links_json to spans
-- =============================================================================

IF COL_LENGTH('spans', 'events_json') IS NULL
    ALTER TABLE spans ADD events_json NVARCHAR(MAX);
IF COL_LENGTH('spans', 'links_json') IS NULL
    ALTER TABLE spans ADD links_json NVARCHAR(MAX);
GO

-- =============================================================================
-- 2.11.0 -- fold existing span_events/span_links rows into spans.events_json/links_json
-- =============================================================================

-- Guarded on OBJECT_ID so this whole block is a no-op on a database already migrated (or
-- created fresh at 2.11.0+, where span_events/span_links never existed).
IF OBJECT_ID('span_events', 'U') IS NOT NULL
BEGIN
    UPDATE s
    SET s.events_json = (
        SELECT
            e.[name]                   AS [Name],
            e.time_unix_nano           AS [TimeUnixNano],
            e.dropped_attributes_count AS [DroppedAttributesCount],
            JSON_QUERY(e.attributes_json) AS [Attributes]
        FROM span_events e
        WHERE e.span_id = s.id
        FOR JSON PATH
    )
    FROM spans s
    WHERE EXISTS (SELECT 1 FROM span_events e WHERE e.span_id = s.id);
END
GO

IF OBJECT_ID('span_links', 'U') IS NOT NULL
BEGIN
    UPDATE s
    SET s.links_json = (
        SELECT
            l.linked_trace_id          AS [LinkedTraceIdHex],
            l.linked_span_id           AS [LinkedSpanIdHex],
            l.trace_state              AS [TraceState],
            l.flags                    AS [Flags],
            l.dropped_attributes_count AS [DroppedAttributesCount],
            JSON_QUERY(l.attributes_json) AS [Attributes]
        FROM span_links l
        WHERE l.span_id = s.id
        FOR JSON PATH
    )
    FROM spans s
    WHERE EXISTS (SELECT 1 FROM span_links l WHERE l.span_id = s.id);
END
GO

-- =============================================================================
-- 2.11.0 -- drop span_events/span_links now that their data lives on
-- spans.events_json/links_json (this also drops fk_span_events_spans/fk_span_links_spans and
-- idx_span_time/idx_span_link, since they belong to the dropped tables)
-- =============================================================================

DROP TABLE IF EXISTS span_events;
DROP TABLE IF EXISTS span_links;
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS tgt
USING (VALUES (N'2.11.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO
