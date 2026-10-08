#!/usr/bin/env bash
#
# Lightweight schema apply runner.
#
# Applies one component's schema script for the chosen provider, skipping the apply when the
# component's target version is already recorded in its version table. There are two components,
# each versioned on its own (plans/control-plane-split.md):
#
#   controlplane  tenants, API keys, alert rules, retention settings   -> control_plane_schema_version
#   telemetry     spans, logs, metrics, rollups                        -> telemetry_schema_version
#
# Usage:
#   schema/apply-schema.sh <controlplane|telemetry> <provider> [database]
#
#   <provider>   controlplane: postgresql | sqlserver | mysql
#                telemetry:    postgresql | sqlserver | clickhouse | mysql
#   [database]   target database name (default: telemetry)
#
# On a relational provider both components normally go to the same database; apply the control
# plane first (a running collector needs keys before it accepts data), though either order works
# (no foreign key crosses the two). A ClickHouse deployment applies the telemetry component to
# ClickHouse and the control plane to a separate PostgreSQL, SQL Server or MySQL database.
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
CONTROLPLANE_TARGET_VERSION="4.0.0"
TELEMETRY_TARGET_VERSION="4.0.0"

usage() {
    echo "usage: $0 <controlplane|telemetry> <provider> [database]" >&2
    echo "  controlplane providers: postgresql|sqlserver|mysql" >&2
    echo "  telemetry providers:    postgresql|sqlserver|clickhouse|mysql" >&2
    exit 2
}

[[ -z "$COMPONENT" || -z "$PROVIDER" ]] && usage

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
            echo "error: ClickHouse has no control-plane schema; use postgresql, sqlserver or mysql for the control plane" >&2
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
