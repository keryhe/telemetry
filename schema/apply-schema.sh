#!/usr/bin/env bash
#
# Lightweight per-provider schema apply runner.
#
# Applies the schema script matching the chosen provider, skipping the apply when
# the target schema version is already recorded in the schema_version table.
#
# Usage:
#   schema/apply-schema.sh <provider> [database]
#
#   <provider>   postgresql | timescale | sqlserver | clickhouse | mysql
#   [database]   target database name (default: telemetry)
#
# Connection settings are taken from the standard client environment variables:
#   postgresql / timescale -> PGHOST, PGPORT, PGUSER, PGPASSWORD (libpq)
#   sqlserver              -> SQLCMDSERVER, SQLCMDUSER, SQLCMDPASSWORD (sqlcmd)
#   clickhouse             -> CLICKHOUSE_HOST, CLICKHOUSE_USER, CLICKHOUSE_PASSWORD (clickhouse-client)
#   mysql                  -> MYSQL_HOST, MYSQL_TCP_PORT, MYSQL_PWD, plus -u via MYSQL_USER (mysql client)
#
# Examples:
#   PGUSER=postgres PGPASSWORD=secret schema/apply-schema.sh timescale
#   SQLCMDSERVER=localhost SQLCMDUSER=sa SQLCMDPASSWORD=secret schema/apply-schema.sh sqlserver
#   CLICKHOUSE_HOST=localhost schema/apply-schema.sh clickhouse
#   MYSQL_HOST=localhost MYSQL_USER=root MYSQL_PWD=secret schema/apply-schema.sh mysql
#
# This runner is for FRESH installs only -- the full schema scripts are not written to be
# re-applied on top of an older schema version. An EXISTING database on an older version must be
# upgraded with the matching script under schema/migrations/ instead, run directly with the
# provider's own client -- for PostgreSQL (plain), that is the single, idempotent
# schema/migrations/PostgreSQL-Migrate.sql, which brings ANY existing version (2.6.0 or later, or
# no schema_version row at all) up to current and is safe to re-run any number of times, e.g.:
#   psql -d telemetry -v ON_ERROR_STOP=1 -f schema/migrations/PostgreSQL-Migrate.sql
# (Other providers still use their own per-version migration scripts under schema/migrations/.)
#
# Timescale: upgrade an existing installation with schema/migrations/Timescale-Migrate.sql
# instead of a chain of per-version files -- it is idempotent (every statement is guarded with
# IF NOT EXISTS / catalog checks, including the TimescaleDB-specific hypertable/compression/
# continuous-aggregate calls) and safe to run, and re-run, against a database at ANY prior schema
# version (2.10.0 through 2.13.3) or a completely fresh, unversioned database:
#   PGUSER=postgres PGPASSWORD=secret psql -d telemetry -v ON_ERROR_STOP=1 \
#       -f schema/migrations/Timescale-Migrate.sql
# The former per-version chain (Timescale-2.10.0-to-2.11.0.sql, -2.11.0-to-2.12.0.sql, ...,
# -2.13.2-to-2.13.3.sql) has been removed now that this single script supersedes it.
#
# SQL Server: upgrade an existing installation with schema/migrations/SqlServer-Migrate.sql
# instead of a chain of per-version files -- every step is guarded (sys.columns/sys.indexes/
# sys.key_constraints/OBJECT_ID existence checks, or a data predicate for the two backfills) and
# it is safe to run, and re-run, against a database at ANY prior schema version (2.6.0 through
# 2.13.3) or a database with no schema_version row at all:
#   sqlcmd -d telemetry -b -I -i schema/migrations/SqlServer-Migrate.sql
# The former per-version chain (SqlServer-2.6.0-to-2.10.0.sql, -2.10.0-to-2.11.0.sql, ...,
# -2.13.2-to-2.13.3.sql) has been removed now that this single script supersedes it.
#
# ClickHouse: upgrade an existing installation with schema/migrations/ClickHouse-Migrate.sql
# instead of a chain of per-version files -- every statement is natively idempotent (IF NOT
# EXISTS / IF EXISTS DDL, ReplacingMergeTree/AggregatingMergeTree-safe backfills) and it is safe
# to run, and re-run, against a database at ANY prior schema version (2.11.0 through 2.13.3) or a
# database that already has the pre-2.11.0 base schema but no schema_version row:
#   CLICKHOUSE_HOST=localhost clickhouse-client --database telemetry --multiquery \
#       < schema/migrations/ClickHouse-Migrate.sql
# The former per-version chain (ClickHouse-2.11.0-to-2.12.0.sql, -2.12.0-to-2.13.0.sql, ...,
# -2.13.2-to-2.13.3.sql) has been removed now that this single script supersedes it.
#
# MySQL: upgrade an existing installation with schema/migrations/MySQL-Migrate.sql instead of a
# chain of per-version files -- every guarded structural change (ADD COLUMN / ADD INDEX / DROP
# INDEX) runs inside a one-off stored procedure that probes information_schema.columns/.statistics
# first (MySQL, unlike MariaDB, has no native IF [NOT] EXISTS on those ALTER TABLE clauses), and it
# is safe to run, and re-run, against a database at ANY prior schema version (2.11.0 through
# 2.13.3) or a completely fresh, unversioned database:
#   mysql -u root telemetry < schema/migrations/MySQL-Migrate.sql
# The former per-version chain (MySQL-2.11.0-to-2.12.0.sql, -2.12.0-to-2.13.0.sql, ...,
# -2.13.2-to-2.13.3.sql) has been removed now that this single script supersedes it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PROVIDER="${1:-}"
DATABASE="${2:-telemetry}"

# Target version must match the value written by the schema scripts.
TARGET_VERSION="2.13.3"

if [[ -z "$PROVIDER" ]]; then
    echo "usage: $0 <postgresql|timescale|sqlserver|clickhouse|mysql> [database]" >&2
    exit 2
fi

case "$PROVIDER" in
    postgresql) SCRIPT_FILE="$SCRIPT_DIR/PostgreSQL-Schema.sql"; ENGINE="psql" ;;
    timescale)  SCRIPT_FILE="$SCRIPT_DIR/Timescale-Schema.sql";  ENGINE="psql" ;;
    sqlserver)  SCRIPT_FILE="$SCRIPT_DIR/SqlServer-Schema.sql";  ENGINE="sqlcmd" ;;
    clickhouse) SCRIPT_FILE="$SCRIPT_DIR/ClickHouse-Schema.sql"; ENGINE="clickhouse" ;;
    mysql)      SCRIPT_FILE="$SCRIPT_DIR/MySQL-Schema.sql";      ENGINE="mysql" ;;
    *)
        echo "error: unknown provider '$PROVIDER' (expected postgresql|timescale|sqlserver|clickhouse|mysql)" >&2
        exit 2
        ;;
esac

if [[ ! -f "$SCRIPT_FILE" ]]; then
    echo "error: schema script not found: $SCRIPT_FILE" >&2
    exit 1
fi

# Returns the current recorded schema version, or empty string if not yet applied.
current_version() {
    case "$ENGINE" in
        psql)
            psql -d "$DATABASE" -tAc \
                "SELECT version FROM schema_version WHERE version = '$TARGET_VERSION'" \
                2>/dev/null || true
            ;;
        sqlcmd)
            sqlcmd -d "$DATABASE" -h -1 -W -Q \
                "SET NOCOUNT ON; SELECT version FROM schema_version WHERE version = '$TARGET_VERSION'" \
                2>/dev/null | tr -d '[:space:]' || true
            ;;
        clickhouse)
            clickhouse-client --database "$DATABASE" -q \
                "SELECT version FROM schema_version WHERE version = '$TARGET_VERSION' LIMIT 1" \
                2>/dev/null | tr -d '[:space:]' || true
            ;;
        mysql)
            mysql -u "${MYSQL_USER:-root}" --batch --skip-column-names "$DATABASE" -e \
                "SELECT version FROM schema_version WHERE version = '$TARGET_VERSION' LIMIT 1" \
                2>/dev/null | tr -d '[:space:]' || true
            ;;
    esac
}

EXISTING="$(current_version)"
if [[ "$EXISTING" == "$TARGET_VERSION" ]]; then
    echo "schema $TARGET_VERSION already applied to '$DATABASE' ($PROVIDER); nothing to do."
    exit 0
fi

echo "applying $PROVIDER schema ($TARGET_VERSION) to '$DATABASE'..."
case "$ENGINE" in
    psql)       psql   -d "$DATABASE" -v ON_ERROR_STOP=1 -f "$SCRIPT_FILE" ;;
    sqlcmd)     sqlcmd -d "$DATABASE" -b -I -i "$SCRIPT_FILE" ;;
    clickhouse) clickhouse-client --database "$DATABASE" --multiquery < "$SCRIPT_FILE" ;;
    mysql)      mysql -u "${MYSQL_USER:-root}" "$DATABASE" < "$SCRIPT_FILE" ;;
esac
echo "done."
