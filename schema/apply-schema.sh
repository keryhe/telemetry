#!/usr/bin/env bash
#
# Lightweight schema apply runner.
#
# Applies one component's schema script for the chosen provider, skipping the apply when the
# component's target version is already recorded in its version table. There are two components,
# each versioned on its own (plans/control-plane-split.md):
#
#   controlplane  tenants (+ retention windows), API keys, alert rules  -> control_plane_schema_version
#   telemetry     spans, logs, metrics, rollups                        -> telemetry_schema_version
#
# Usage:
#   schema/apply-schema.sh <controlplane|telemetry> <provider> [database]
#
#   <provider>   controlplane: postgresql | sqlserver | mysql | sqlite
#                telemetry:    postgresql | sqlserver | clickhouse | mysql | parquet
#   [database]   target database name (default: telemetry); for parquet, the root directory (required);
#                for sqlite, the database file (required; a missing directory is created)
#
# On a relational provider both components normally go to the same database; apply the control
# plane first (a running collector needs keys before it accepts data), though either order works
# (no foreign key crosses the two). A ClickHouse deployment applies the telemetry component to
# ClickHouse and the control plane to a separate PostgreSQL, SQL Server or MySQL database.
#
# The parquet provider has no scripts: `telemetry parquet <root>` only creates the root directory. Whichever host
# starts first (collector or API) writes <root>/_schema.json (the version and a hash of its table definitions), and
# both hosts refuse a root with another version (plans/parquet-duckdb). It has no control plane: run `controlplane` on a
# relational provider.
#
# The sqlite provider is for the control plane only (it lets a Parquet deployment run with no database server).
# `controlplane sqlite <file>` needs the sqlite3 CLI and CREATES a fresh file; it never migrates. Both hosts do that
# themselves at startup (and create the file and its directory too, so running this first is optional): an older file is
# refused here with "start a host to migrate it", a newer one is refused outright. Every host and tool must use the same
# absolute path as `ConnectionStrings:ControlPlane` (`Data Source=<file>`) on the same machine's local file system.
#
# Connection settings are taken from the standard client environment variables:
#   postgresql  -> PGHOST, PGPORT, PGUSER, PGPASSWORD (libpq)
#   sqlserver   -> SQLCMDSERVER, SQLCMDUSER, SQLCMDPASSWORD (sqlcmd)
#   clickhouse  -> CLICKHOUSE_HOST, CLICKHOUSE_USER, CLICKHOUSE_PASSWORD (clickhouse-client)
#   mysql       -> MYSQL_HOST, MYSQL_TCP_PORT, MYSQL_PWD, plus -u via MYSQL_USER (mysql client)
#
# Examples:
#   PGUSER=postgres PGPASSWORD=secret schema/apply-schema.sh controlplane postgresql
#   PGUSER=postgres PGPASSWORD=secret schema/apply-schema.sh telemetry postgresql
#   CLICKHOUSE_HOST=localhost schema/apply-schema.sh telemetry clickhouse
#   schema/apply-schema.sh controlplane sqlite /var/lib/keryhe/control-plane.db
#
# Schema 4.0.0 is a FRESH-INSTALL schema for both components: there is no migration from 2.x or
# 3.x, and the scripts are not written to be re-applied on top of another version. This runner
# skips the apply only when the component's target version row is already recorded.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

COMPONENT="${1:-}"
PROVIDER="${2:-}"
DATABASE="${3:-telemetry}"

# Target versions must match the values written by the schema scripts. A schema change edits the
# affected component's scripts and that component's version, in one commit.
CONTROLPLANE_TARGET_VERSION="4.0.1"
TELEMETRY_TARGET_VERSION="4.0.1"

usage() {
    echo "usage: $0 <controlplane|telemetry> <provider> [database]" >&2
    echo "  controlplane providers: postgresql|sqlserver|mysql|sqlite" >&2
    echo "  telemetry providers:    postgresql|sqlserver|clickhouse|mysql|parquet" >&2
    exit 2
}

[[ -z "$COMPONENT" || -z "$PROVIDER" ]] && usage

if [[ "$PROVIDER" == "sqlite" ]]; then
    if [[ "$COMPONENT" != "controlplane" ]]; then
        echo "error: SQLite is a control-plane provider only; use postgresql, sqlserver, clickhouse or parquet for the telemetry data" >&2
        exit 2
    fi
    if [[ $# -lt 3 ]]; then
        echo "error: controlplane sqlite needs the database file: $0 controlplane sqlite <file>" >&2
        exit 2
    fi
    if ! command -v sqlite3 >/dev/null 2>&1; then
        echo "error: the sqlite3 command-line tool is required (or start the API host, which creates the database itself)" >&2
        exit 1
    fi
    SQLITE_DIR="$(dirname "$DATABASE")"
    mkdir -p "$SQLITE_DIR"
    SQLITE_FILE="$(cd "$SQLITE_DIR" && pwd)/$(basename "$DATABASE")"
    SQLITE_SCRIPT="$SCRIPT_DIR/SQLite-ControlPlane.sql"
    if [[ ! -f "$SQLITE_SCRIPT" ]]; then
        echo "error: schema script not found: $SQLITE_SCRIPT" >&2
        exit 1
    fi
    if [[ -f "$SQLITE_FILE" ]]; then
        RECORDED="$(sqlite3 "$SQLITE_FILE" "SELECT version FROM control_plane_schema_version" 2>/dev/null | sort -V | tail -1 || true)"
        if [[ "$RECORDED" == "$CONTROLPLANE_TARGET_VERSION" ]]; then
            echo "controlplane schema $CONTROLPLANE_TARGET_VERSION already applied to '$SQLITE_FILE' (sqlite); nothing to do."
            exit 0
        elif [[ -n "$RECORDED" ]]; then
            if [[ "$(printf '%s\n%s\n' "$RECORDED" "$CONTROLPLANE_TARGET_VERSION" | sort -V | head -1)" == "$RECORDED" ]]; then
                echo "error: '$SQLITE_FILE' is at schema version $RECORDED, older than $CONTROLPLANE_TARGET_VERSION; this script only creates. Start the collector or the API host to migrate it." >&2
            else
                echo "error: '$SQLITE_FILE' is at schema version $RECORDED, newer than $CONTROLPLANE_TARGET_VERSION; downgrades are not supported." >&2
            fi
            exit 1
        fi
    fi
    echo "applying sqlite controlplane schema ($CONTROLPLANE_TARGET_VERSION) to '$SQLITE_FILE'..."
    sqlite3 -bail "$SQLITE_FILE" < "$SQLITE_SCRIPT" > /dev/null
    echo "done. Use ConnectionStrings:ControlPlane = \"Data Source=$SQLITE_FILE\" on every host and tool, on this machine."
    exit 0
fi

if [[ "$PROVIDER" == "parquet" ]]; then
    if [[ "$COMPONENT" != "telemetry" ]]; then
        echo "error: Parquet has no control-plane schema; use postgresql, sqlserver, mysql or sqlite for the control plane" >&2
        exit 2
    fi
    if [[ $# -lt 3 ]]; then
        echo "error: telemetry parquet needs the root directory: $0 telemetry parquet <root>" >&2
        exit 2
    fi
    mkdir -p "$DATABASE"
    echo "created Parquet root '$DATABASE'; the collector writes _schema.json ($TELEMETRY_TARGET_VERSION) on first start."
    exit 0
fi

case "$COMPONENT" in
    controlplane) FILE_SUFFIX="ControlPlane"; TARGET_VERSION="$CONTROLPLANE_TARGET_VERSION"; VERSION_TABLE="control_plane_schema_version" ;;
    telemetry)    FILE_SUFFIX="Telemetry";    TARGET_VERSION="$TELEMETRY_TARGET_VERSION";    VERSION_TABLE="telemetry_schema_version" ;;
    *) echo "error: unknown component '$COMPONENT' (expected controlplane|telemetry)" >&2; usage ;;
esac

case "$PROVIDER" in
    postgresql) SCRIPT_FILE="$SCRIPT_DIR/PostgreSQL-$FILE_SUFFIX.sql"; ENGINE="psql" ;;
    sqlserver)  SCRIPT_FILE="$SCRIPT_DIR/SqlServer-$FILE_SUFFIX.sql";  ENGINE="sqlcmd" ;;
    mysql)      SCRIPT_FILE="$SCRIPT_DIR/MySQL-$FILE_SUFFIX.sql";      ENGINE="mysql" ;;
    clickhouse)
        if [[ "$COMPONENT" == "controlplane" ]]; then
            echo "error: ClickHouse has no control-plane schema; use postgresql, sqlserver, mysql or sqlite for the control plane" >&2
            exit 2
        fi
        SCRIPT_FILE="$SCRIPT_DIR/ClickHouse-Telemetry.sql"; ENGINE="clickhouse" ;;
    *)
        echo "error: unknown provider '$PROVIDER' (expected postgresql|sqlserver|clickhouse|mysql)" >&2
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
                "SELECT version FROM $VERSION_TABLE WHERE version = '$TARGET_VERSION'" \
                2>/dev/null || true
            ;;
        sqlcmd)
            sqlcmd -d "$DATABASE" -h -1 -W -Q \
                "SET NOCOUNT ON; SELECT version FROM $VERSION_TABLE WHERE version = '$TARGET_VERSION'" \
                2>/dev/null | tr -d '[:space:]' || true
            ;;
        clickhouse)
            clickhouse-client --database "$DATABASE" -q \
                "SELECT version FROM $VERSION_TABLE WHERE version = '$TARGET_VERSION' LIMIT 1" \
                2>/dev/null | tr -d '[:space:]' || true
            ;;
        mysql)
            mysql -u "${MYSQL_USER:-root}" --batch --skip-column-names "$DATABASE" -e \
                "SELECT version FROM $VERSION_TABLE WHERE version = '$TARGET_VERSION' LIMIT 1" \
                2>/dev/null | tr -d '[:space:]' || true
            ;;
    esac
}

EXISTING="$(current_version)"
if [[ "$EXISTING" == "$TARGET_VERSION" ]]; then
    echo "$COMPONENT schema $TARGET_VERSION already applied to '$DATABASE' ($PROVIDER); nothing to do."
    exit 0
fi

echo "applying $PROVIDER $COMPONENT schema ($TARGET_VERSION) to '$DATABASE'..."
case "$ENGINE" in
    psql)       psql   -d "$DATABASE" -v ON_ERROR_STOP=1 -f "$SCRIPT_FILE" ;;
    sqlcmd)     sqlcmd -d "$DATABASE" -b -I -i "$SCRIPT_FILE" ;;
    clickhouse) clickhouse-client --database "$DATABASE" --multiquery < "$SCRIPT_FILE" ;;
    mysql)      mysql -u "${MYSQL_USER:-root}" "$DATABASE" < "$SCRIPT_FILE" ;;
esac
echo "done."
