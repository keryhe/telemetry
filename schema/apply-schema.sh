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
#   <provider>   postgresql | timescale | sqlserver
#   [database]   target database name (default: telemetry)
#
# Connection settings are taken from the standard client environment variables:
#   postgresql / timescale -> PGHOST, PGPORT, PGUSER, PGPASSWORD (libpq)
#   sqlserver              -> SQLCMDSERVER, SQLCMDUSER, SQLCMDPASSWORD (sqlcmd)
#
# Examples:
#   PGUSER=postgres PGPASSWORD=secret schema/apply-schema.sh timescale
#   SQLCMDSERVER=localhost SQLCMDUSER=sa SQLCMDPASSWORD=secret schema/apply-schema.sh sqlserver

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PROVIDER="${1:-}"
DATABASE="${2:-telemetry}"

# Target version must match the value written by the schema scripts.
TARGET_VERSION="2.5.0"

if [[ -z "$PROVIDER" ]]; then
    echo "usage: $0 <postgresql|timescale|sqlserver> [database]" >&2
    exit 2
fi

case "$PROVIDER" in
    postgresql) SCRIPT_FILE="$SCRIPT_DIR/PostgreSQL-Schema.sql"; ENGINE="psql" ;;
    timescale)  SCRIPT_FILE="$SCRIPT_DIR/Timescale-Schema.sql";  ENGINE="psql" ;;
    sqlserver)  SCRIPT_FILE="$SCRIPT_DIR/SqlServer-Schema.sql";  ENGINE="sqlcmd" ;;
    *)
        echo "error: unknown provider '$PROVIDER' (expected postgresql|timescale|sqlserver)" >&2
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
    esac
}

EXISTING="$(current_version)"
if [[ "$EXISTING" == "$TARGET_VERSION" ]]; then
    echo "schema $TARGET_VERSION already applied to '$DATABASE' ($PROVIDER); nothing to do."
    exit 0
fi

echo "applying $PROVIDER schema ($TARGET_VERSION) to '$DATABASE'..."
case "$ENGINE" in
    psql)   psql   -d "$DATABASE" -v ON_ERROR_STOP=1 -f "$SCRIPT_FILE" ;;
    sqlcmd) sqlcmd -d "$DATABASE" -b -i "$SCRIPT_FILE" ;;
esac
echo "done."
