#!/usr/bin/env bash
# Publish and activate the Mir3 web portal without changing MirDB or Nginx.
set -Eeuo pipefail
umask 027

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)

SERVICE_NAME=${MIR3_WEB_SERVICE_NAME:-mir3-web}
SERVICE_USER=${MIR3_WEB_SERVICE_USER:-mir3-web}
SERVICE_GROUP=${MIR3_WEB_SERVICE_GROUP:-mir3-web}
WEB_ROOT=${MIR3_WEB_ROOT:-/opt/mir3-web}
RELEASES_DIR=${MIR3_WEB_RELEASES_DIR:-$WEB_ROOT/releases}
BACKUPS_DIR=${MIR3_WEB_BACKUPS_DIR:-$WEB_ROOT/backups}
APP_LINK=${MIR3_WEB_APP_LINK:-$WEB_ROOT/app}
DOWNLOADS_DIR=${MIR3_WEB_DOWNLOADS_DIR:-$WEB_ROOT/downloads}
PATCH_ROOT=${MIR3_WEB_PATCH_ROOT:-$WEB_ROOT/patch}
PATCH_RELEASES_DIR=${MIR3_WEB_PATCH_RELEASES_DIR:-$PATCH_ROOT/releases}
LIB_DIR=${MIR3_WEB_LIB_DIR:-/var/lib/mir3-web}
LOCK_DIR=${MIR3_WEB_LOCK_DIR:-$LIB_DIR/verification-locks}
QUEUE_DIR=${MIR3_WEB_QUEUE_DIR:-/opt/zircon/account-queue}
ENV_DIR=${MIR3_WEB_ENV_DIR:-/etc/mir3-web}
ENV_FILE=${MIR3_WEB_ENV_FILE:-$ENV_DIR/mir3-web.env}
SYSTEMD_DIR=${MIR3_WEB_SYSTEMD_DIR:-/etc/systemd/system}
UNIT_SOURCE=${MIR3_WEB_UNIT_SOURCE:-$SCRIPT_DIR/mir3-web.service}
UNIT_PATH=${MIR3_WEB_UNIT_PATH:-$SYSTEMD_DIR/$SERVICE_NAME.service}
DEPLOYMENT_LOCK_FILE=${MIR3_WEB_DEPLOYMENT_LOCK_FILE:-/run/lock/mir3-web-deployment.lock}
DOTNET_COMMAND=${DOTNET_COMMAND:-/opt/dotnet/dotnet}
PORT=${MIR3_WEB_PORT:-8088}
HEALTH_URL=${MIR3_WEB_HEALTH_URL:-http://127.0.0.1:$PORT/healthz}
MIN_FREE_BYTES=${MIR3_WEB_MIN_FREE_BYTES:-1073741824}
VERIFY_RETRIES=${MIR3_WEB_VERIFY_RETRIES:-30}
PORTAL_DB=$LIB_DIR/portal.db

usage() {
    printf 'Usage: %s [repository-source-root]\n' "${BASH_SOURCE[0]}" >&2
}

die() {
    printf 'mir3-web install: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

require_absolute_safe_path() {
    local path=$1
    [[ -n "$path" && "$path" == /* ]] || die "unsafe path: $path"
    [[ "$path" != */.. && "$path" != */../* && "$path" != ../* ]] || die "unsafe path: $path"
}

reject_symlink_components() {
    local path=$1
    local current=/ component
    local -a components=()
    IFS=/ read -r -a components <<< "${path#/}"
    for component in "${components[@]}"; do
        [[ -n "$component" ]] || continue
        current=$current/$component
        [[ ! -L "$current" ]] || die "unsafe symlink path component: $current"
        [[ ! -e "$current" || -d "$current" ]] || die "unsafe non-directory path component: $current"
    done
}

validate_directory_layout() {
    local web_root_real releases_real backups_real
    for directory in "$WEB_ROOT" "$RELEASES_DIR" "$BACKUPS_DIR"; do
        reject_symlink_components "$directory"
    done
    web_root_real=$(readlink -f -- "$WEB_ROOT") || die "cannot canonicalize web root: $WEB_ROOT"
    releases_real=$(readlink -f -- "$RELEASES_DIR") || die "cannot canonicalize releases directory: $RELEASES_DIR"
    backups_real=$(readlink -f -- "$BACKUPS_DIR") || die "cannot canonicalize backups directory: $BACKUPS_DIR"
    [[ -d "$web_root_real" && ! -L "$web_root_real" ]] || die "web root is not a directory: $WEB_ROOT"
    [[ -d "$releases_real" && ! -L "$releases_real" ]] || die "releases directory is not a directory: $RELEASES_DIR"
    [[ -d "$backups_real" && ! -L "$backups_real" ]] || die "backups directory is not a directory: $BACKUPS_DIR"
    [[ "$releases_real" != "$web_root_real" && "$releases_real" == "$web_root_real/"* ]] || die "releases directory escapes web root"
    [[ "$backups_real" != "$web_root_real" && "$backups_real" == "$web_root_real/"* ]] || die "backups directory escapes web root"
}

check_disk_space() {
    local check_path=$1
    local available_kib
    while [[ ! -e "$check_path" ]]; do
        [[ "$check_path" != / ]] || break
        check_path=$(dirname -- "$check_path")
    done
    available_kib=$(df -Pk "$check_path" | awk 'NR == 2 { print $4 }')
    [[ "$available_kib" =~ ^[0-9]+$ ]] || die "unable to determine free disk space at $check_path"
    (( available_kib * 1024 >= MIN_FREE_BYTES )) || die "less than $MIN_FREE_BYTES free bytes at $check_path"
}

check_port() {
    local listener
    listener=$(ss -Hln "sport = :$PORT" 2>/dev/null || true)
    if [[ -n "$listener" ]] && ! systemctl is-active --quiet "$SERVICE_NAME"; then
        die "TCP port $PORT is already in use by another service"
    fi
}

resolve_current_release() {
    local resolved
    if [[ ! -e "$APP_LINK" && ! -L "$APP_LINK" ]]; then
        return 0
    fi
    [[ -L "$APP_LINK" ]] || die "current app path exists but is not a symlink: $APP_LINK"
    resolved=$(readlink -f -- "$APP_LINK") || die "cannot resolve current app symlink: $APP_LINK"
    [[ -d "$resolved" ]] || die "current app symlink target is missing: $resolved"
    [[ "$resolved" == "$RELEASES_DIR/"* ]] || die "current app symlink points outside releases: $resolved"
    printf '%s\n' "$resolved"
}

backup_current_state() {
    local backup_dir=$1
    local current_release=$2
    install -d -o root -g root -m 0750 "$backup_dir"

    if [[ -n "$current_release" ]]; then
        install -d -o root -g root -m 0755 "$backup_dir/app"
        cp -a -- "$current_release/." "$backup_dir/app/"
        printf '%s\n' "$current_release" > "$backup_dir/app-target"
    fi
    if [[ -f "$UNIT_PATH" && ! -L "$UNIT_PATH" ]]; then
        replace_regular_file "$UNIT_PATH" "$backup_dir/mir3-web.service" 0644 || die "cannot safely back up unit file"
    fi
    if [[ -f "$ENV_FILE" && ! -L "$ENV_FILE" ]]; then
        replace_regular_file "$ENV_FILE" "$backup_dir/mir3-web.env" 0600 || die "cannot safely back up environment file"
    fi
}

acquire_deployment_lock() {
    local lock_parent
    lock_parent=$(dirname -- "$DEPLOYMENT_LOCK_FILE")
    reject_symlink_components "$lock_parent"
    if [[ -e "$DEPLOYMENT_LOCK_FILE" || -L "$DEPLOYMENT_LOCK_FILE" ]]; then
        [[ -f "$DEPLOYMENT_LOCK_FILE" && ! -L "$DEPLOYMENT_LOCK_FILE" ]] || die "deployment lock is not a regular file: $DEPLOYMENT_LOCK_FILE"
    fi
    exec {DEPLOYMENT_LOCK_FD}>>"$DEPLOYMENT_LOCK_FILE" || die "cannot open deployment lock: $DEPLOYMENT_LOCK_FILE"
    flock -n "$DEPLOYMENT_LOCK_FD" || die "another Mir3 web deployment or rollback is already running"
    chown --no-dereference root:root "$DEPLOYMENT_LOCK_FILE"
    chmod 0600 -- "$DEPLOYMENT_LOCK_FILE"
}

ensure_env_file_safe() {
    [[ -f "$ENV_FILE" && ! -L "$ENV_FILE" ]] || die "runtime environment file must be a regular non-symlink file: $ENV_FILE"
}

replace_regular_file() {
    local source=$1
    local destination=$2
    local mode=$3
    local temporary

    [[ -f "$source" && ! -L "$source" ]] || return 1
    if [[ -L "$destination" ]]; then
        if [[ "$destination" == "$UNIT_PATH" && "${SERVICE_ENABLED_STATE:-}" == masked* ]]; then
            [[ "$(readlink -- "$destination")" == /dev/null ]] || return 1
        elif [[ "$destination" == "$UNIT_PATH" && "${SERVICE_ENABLED_STATE:-}" == linked ]]; then
            [[ -n "${ORIGINAL_UNIT_LINK_TARGET:-}" && "$(readlink -- "$destination")" == "$ORIGINAL_UNIT_LINK_TARGET" ]] || return 1
        else
            return 1
        fi
    elif [[ -e "$destination" ]]; then
        [[ -f "$destination" ]] || return 1
    fi
    temporary=$(mktemp "$(dirname -- "$destination")/.${SERVICE_NAME}.replace.XXXXXX") || return 1
    if ! install -o root -g root -m "$mode" -- "$source" "$temporary"; then
        rm -f -- "$temporary"
        return 1
    fi
    if ! mv -Tf -- "$temporary" "$destination"; then
        rm -f -- "$temporary"
        return 1
    fi
}

restore_unit_link() {
    local target=$1
    local temporary
    temporary=$(mktemp "$(dirname -- "$UNIT_PATH")/.${SERVICE_NAME}.link.XXXXXX") || return 1
    rm -f -- "$temporary" || return 1
    if ! ln -s -- "$target" "$temporary"; then
        rm -f -- "$temporary"
        return 1
    fi
    if ! mv -Tf -- "$temporary" "$UNIT_PATH"; then
        rm -f -- "$temporary"
        return 1
    fi
}

run_database_migration() {
    local migration_env_file migration_status

    require_command systemd-run
    migration_env_file=$(mktemp "$ENV_DIR/.migration-env.XXXXXX") || die "cannot create migration environment override"
    chmod 0600 -- "$migration_env_file"
    if ! printf 'ConnectionStrings__Portal=Data Source=%s\n' "$PORTAL_DB" > "$migration_env_file"; then
        rm -f -- "$migration_env_file"
        die "cannot write migration environment override"
    fi
    [[ -f "$migration_env_file" && ! -L "$migration_env_file" ]] || die "migration environment override is not a regular file"

    # systemd applies EnvironmentFile entries in order; this final file pins EF to PORTAL_DB.
    if systemd-run --quiet --wait --pipe --collect \
        --unit="$SERVICE_NAME-migration-$RELEASE_TIMESTAMP" \
        --uid="$SERVICE_USER" \
        --gid="$SERVICE_GROUP" \
        --property="EnvironmentFile=$ENV_FILE" \
        --property="EnvironmentFile=$migration_env_file" \
        -- "$DOTNET_COMMAND" ef database update \
        --project "$PROJECT_FILE" \
        --startup-project "$PROJECT_FILE" \
        --configuration Release \
        --no-build; then
        migration_status=0
    else
        migration_status=$?
    fi
    rm -f -- "$migration_env_file" || die "cannot remove migration environment override"
    return "$migration_status"
}

ensure_database_path_safe() {
    local database_artifact
    for database_artifact in "$PORTAL_DB" "$PORTAL_DB-wal" "$PORTAL_DB-shm"; do
        if [[ -e "$database_artifact" || -L "$database_artifact" ]]; then
            [[ -f "$database_artifact" && ! -L "$database_artifact" ]] || die "database artifact must be a regular non-symlink file: $database_artifact"
        fi
    done
}

validate_portal_database_path() {
    require_absolute_safe_path "$PORTAL_DB"
    [[ "$PORTAL_DB" == "$LIB_DIR/portal.db" ]] || die "portal database path must remain in the service library directory"
    reject_symlink_components "$(dirname -- "$PORTAL_DB")"
    ensure_database_path_safe
}

ensure_database_ownership() {
    local database_artifact service_uid service_gid
    service_uid=$(id -u "$SERVICE_USER")
    service_gid=$(id -g "$SERVICE_USER")
    [[ -f "$PORTAL_DB" && ! -L "$PORTAL_DB" ]] || die "database migration did not create a regular portal database: $PORTAL_DB"
    for database_artifact in "$PORTAL_DB" "$PORTAL_DB-wal" "$PORTAL_DB-shm"; do
        if [[ -e "$database_artifact" || -L "$database_artifact" ]]; then
            [[ -f "$database_artifact" && ! -L "$database_artifact" ]] || die "database artifact is not a regular non-symlink file: $database_artifact"
            chown --no-dereference "$SERVICE_USER:$SERVICE_GROUP" "$database_artifact"
            [[ -f "$database_artifact" && ! -L "$database_artifact" ]] || die "database artifact changed during ownership update: $database_artifact"
            [[ "$(stat -c '%u:%g' -- "$database_artifact")" == "$service_uid:$service_gid" ]] || die "database artifact ownership is incorrect: $database_artifact"
        fi
    done
}

prepare_database_ownership() {
    local database_artifact service_uid service_gid
    service_uid=$(id -u "$SERVICE_USER")
    service_gid=$(id -g "$SERVICE_USER")
    for database_artifact in "$PORTAL_DB" "$PORTAL_DB-wal" "$PORTAL_DB-shm"; do
        if [[ -e "$database_artifact" || -L "$database_artifact" ]]; then
            [[ -f "$database_artifact" && ! -L "$database_artifact" ]] || die "database artifact must be a regular non-symlink file: $database_artifact"
            chown --no-dereference "$SERVICE_USER:$SERVICE_GROUP" "$database_artifact"
            [[ -f "$database_artifact" && ! -L "$database_artifact" ]] || die "database artifact changed during ownership update: $database_artifact"
            [[ "$(stat -c '%u:%g' -- "$database_artifact")" == "$service_uid:$service_gid" ]] || die "database artifact ownership is incorrect before migration: $database_artifact"
        fi
    done
}

verify_service() {
    local attempt
    for ((attempt = 1; attempt <= VERIFY_RETRIES; attempt++)); do
        if curl --fail --silent --show-error --max-time 5 "$HEALTH_URL" >/dev/null \
            && ss -Hln "sport = :$PORT" 2>/dev/null | grep -q .; then
            return 0
        fi
        sleep 1
    done
    die "service did not become healthy on $HEALTH_URL and TCP port $PORT"
}

verify_enabled_state() {
    local observed_state
    observed_state=$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)
    observed_state=${observed_state%%$'\n'*}
    [[ -n "$observed_state" ]] || observed_state=not-found
    [[ "$observed_state" == "$SERVICE_ENABLED_STATE" ]] || {
        printf 'mir3-web install: expected systemd enable state %s, got %s\n' "$SERVICE_ENABLED_STATE" "${observed_state:-empty}" >&2
        return 1
    }
}

restore_enabled_state() {
    case "$SERVICE_ENABLED_STATE" in
        enabled)
            systemctl unmask "$SERVICE_NAME" >/dev/null 2>&1
            systemctl disable "$SERVICE_NAME" >/dev/null 2>&1
            systemctl enable "$SERVICE_NAME" >/dev/null 2>&1
            ;;
        enabled-runtime)
            systemctl unmask "$SERVICE_NAME" >/dev/null 2>&1
            systemctl disable "$SERVICE_NAME" >/dev/null 2>&1
            systemctl enable --runtime "$SERVICE_NAME" >/dev/null 2>&1
            ;;
        linked)
            systemctl unmask "$SERVICE_NAME" >/dev/null 2>&1
            systemctl disable "$SERVICE_NAME" >/dev/null 2>&1
            if [[ ! -L "$UNIT_PATH" ]]; then
                systemctl link "$UNIT_PATH" >/dev/null 2>&1
            fi
            ;;
        masked)
            systemctl mask "$SERVICE_NAME" >/dev/null 2>&1
            ;;
        masked-runtime)
            systemctl mask --runtime "$SERVICE_NAME" >/dev/null 2>&1
            ;;
        disabled)
            systemctl unmask "$SERVICE_NAME" >/dev/null 2>&1
            systemctl disable "$SERVICE_NAME" >/dev/null 2>&1
            ;;
        not-found)
            systemctl disable "$SERVICE_NAME" >/dev/null 2>&1
            ;;
        *)
            printf 'mir3-web install: unsupported systemd enable state: %s\n' "$SERVICE_ENABLED_STATE" >&2
            return 1
            ;;
    esac
    verify_enabled_state
}

if [[ $# -gt 1 ]]; then
    usage
    exit 2
fi
(( EUID == 0 )) || die "must be run as root"

SOURCE_ROOT=${1:-$REPOSITORY_ROOT}
SOURCE_ROOT=$(CDPATH= cd -- "$SOURCE_ROOT" && pwd) || die "source root does not exist: ${1:-$REPOSITORY_ROOT}"
PROJECT_FILE=$SOURCE_ROOT/Mir3.Web/Mir3.Web.csproj
[[ -f "$PROJECT_FILE" ]] || die "Mir3.Web project not found: $PROJECT_FILE"
[[ -f "$UNIT_SOURCE" ]] || die "systemd unit source not found: $UNIT_SOURCE"
[[ -f "$ENV_FILE" && ! -L "$ENV_FILE" ]] || die "runtime environment file must be a regular non-symlink file: $ENV_FILE"
require_command systemctl
require_command readlink
if [[ -L "$UNIT_PATH" ]]; then
    unit_enabled_state=$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)
    unit_enabled_state=${unit_enabled_state%%$'\n'*}
    case "$unit_enabled_state" in
        masked|masked-runtime)
            [[ "$(readlink -- "$UNIT_PATH")" == /dev/null ]] || die "systemd unit path must be a regular non-symlink file: $UNIT_PATH"
            ;;
        linked)
            unit_link_target=$(readlink -- "$UNIT_PATH")
            require_absolute_safe_path "$unit_link_target"
            reject_symlink_components "$(dirname -- "$unit_link_target")"
            [[ -f "$unit_link_target" && ! -L "$unit_link_target" ]] || die "linked systemd unit target must be a regular non-symlink file: $UNIT_PATH"
            ;;
        *)
            die "systemd unit path must be a regular non-symlink file: $UNIT_PATH"
            ;;
    esac
elif [[ -e "$UNIT_PATH" ]]; then
    [[ -f "$UNIT_PATH" ]] || die "systemd unit path must be a regular non-symlink file: $UNIT_PATH"
fi

for configured_path in "$WEB_ROOT" "$RELEASES_DIR" "$BACKUPS_DIR" "$APP_LINK" "$DOWNLOADS_DIR" \
    "$PATCH_ROOT" "$PATCH_RELEASES_DIR" \
    "$LIB_DIR" "$LOCK_DIR" "$QUEUE_DIR" "$ENV_DIR" "$ENV_FILE" "$SYSTEMD_DIR" "$UNIT_SOURCE" "$UNIT_PATH" "$DEPLOYMENT_LOCK_FILE" "$PORTAL_DB"; do
    require_absolute_safe_path "$configured_path"
done
[[ "$RELEASES_DIR" == "$WEB_ROOT/"* ]] || die "releases directory must be below web root"
[[ "$PATCH_RELEASES_DIR" == "$PATCH_ROOT/"* ]] || die "patch releases directory must be below the patch root"
[[ "$BACKUPS_DIR" == "$WEB_ROOT/"* ]] || die "backups directory must be below web root"
[[ "$APP_LINK" == "$WEB_ROOT/app" ]] || die "app symlink must be exactly web-root/app"
[[ "$PORTAL_DB" == "$LIB_DIR/portal.db" ]] || die "portal database path must remain in the service library directory"
[[ "$PORT" =~ ^[0-9]+$ && "$PORT" -ge 1 && "$PORT" -le 65535 ]] || die "invalid port: $PORT"
[[ "$MIN_FREE_BYTES" =~ ^[0-9]+$ ]] || die "invalid minimum free-byte value"
[[ "$VERIFY_RETRIES" =~ ^[1-9][0-9]*$ ]] || die "invalid verification retry count"

require_command awk
require_command chown
require_command chmod
require_command cp
require_command curl
require_command date
require_command df
require_command dirname
require_command flock
require_command grep
require_command install
require_command mv
require_command readlink
require_command rm
require_command sleep
require_command ss
require_command systemctl
require_command stat
require_command getent
require_command groupadd
require_command id
require_command ln
require_command mktemp
require_command useradd
require_command "$DOTNET_COMMAND"
reject_symlink_components "$WEB_ROOT"
reject_symlink_components "$RELEASES_DIR"
reject_symlink_components "$BACKUPS_DIR"
reject_symlink_components "$DOWNLOADS_DIR"
reject_symlink_components "$PATCH_ROOT"
reject_symlink_components "$PATCH_RELEASES_DIR"
reject_symlink_components "$LIB_DIR"
reject_symlink_components "$LOCK_DIR"
reject_symlink_components "$QUEUE_DIR"
reject_symlink_components "$ENV_DIR"
reject_symlink_components "$SYSTEMD_DIR"
reject_symlink_components "$(dirname -- "$APP_LINK")"
reject_symlink_components "$(dirname -- "$ENV_FILE")"
reject_symlink_components "$(dirname -- "$UNIT_PATH")"
reject_symlink_components "$(dirname -- "$PORTAL_DB")"
"$DOTNET_COMMAND" --version >/dev/null || die "dotnet SDK is unavailable: $DOTNET_COMMAND"
check_disk_space "$(dirname -- "$WEB_ROOT")"
check_port
acquire_deployment_lock

getent group "$SERVICE_GROUP" >/dev/null 2>&1 || groupadd --system "$SERVICE_GROUP"
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --gid "$SERVICE_GROUP" --home-dir /nonexistent --shell /usr/sbin/nologin "$SERVICE_USER"
fi
[[ "$(id -gn "$SERVICE_USER")" == "$SERVICE_GROUP" ]] || die "user $SERVICE_USER is not in group $SERVICE_GROUP"

install -d -o root -g root -m 0755 "$WEB_ROOT" "$RELEASES_DIR" "$BACKUPS_DIR" "$PATCH_ROOT" "$PATCH_RELEASES_DIR"
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$LIB_DIR" "$DOWNLOADS_DIR"
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0700 "$LOCK_DIR"
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0770 "$QUEUE_DIR"
install -d -o root -g root -m 0750 "$ENV_DIR" "$SYSTEMD_DIR"
validate_directory_layout
ensure_env_file_safe
chown --no-dereference root:root "$ENV_FILE"
ensure_env_file_safe
chmod 0600 -- "$ENV_FILE"
ensure_env_file_safe

RELEASE_TIMESTAMP=$(date -u +%Y%m%d%H%M%S)
if [[ -e "$RELEASES_DIR/$RELEASE_TIMESTAMP" ]]; then
    RELEASE_TIMESTAMP="$RELEASE_TIMESTAMP-$$"
fi
RELEASE_DIR=$RELEASES_DIR/$RELEASE_TIMESTAMP
BACKUP_DIR=$BACKUPS_DIR/$RELEASE_TIMESTAMP
reject_symlink_components "$RELEASE_DIR"
reject_symlink_components "$BACKUP_DIR"
STAGING_DIR=$(mktemp -d "$RELEASES_DIR/.staging-$RELEASE_TIMESTAMP.XXXXXX")
CURRENT_RELEASE=$(resolve_current_release)
LINK_SWITCHED=0
SERVICE_STARTED=0
UNIT_INSTALLED=0
SERVICE_WAS_ACTIVE=0
SERVICE_ENABLED_STATE=$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)
[[ -n "$SERVICE_ENABLED_STATE" ]] || SERVICE_ENABLED_STATE=not-found
SERVICE_ENABLED_STATE=${SERVICE_ENABLED_STATE%%$'\n'*}
case "$SERVICE_ENABLED_STATE" in
    enabled|enabled-runtime|linked|masked|masked-runtime|disabled|not-found) ;;
    *) die "unsupported systemd enable state: $SERVICE_ENABLED_STATE" ;;
esac
ORIGINAL_UNIT_LINK_TARGET=
if [[ "$SERVICE_ENABLED_STATE" == linked && -L "$UNIT_PATH" ]]; then
    ORIGINAL_UNIT_LINK_TARGET=$(readlink -- "$UNIT_PATH")
fi
if systemctl is-active --quiet "$SERVICE_NAME"; then
    SERVICE_WAS_ACTIVE=1
fi

restore_on_failure() {
    local status=$? recovery_status=0
    trap - EXIT
    if (( status != 0 )); then
        if ! systemctl stop "$SERVICE_NAME" >/dev/null 2>&1; then
            printf 'mir3-web install: recovery failed while stopping service\n' >&2
            recovery_status=1
        fi
        if (( LINK_SWITCHED )); then
            if [[ -n "$CURRENT_RELEASE" ]]; then
                if ! ln -s -- "$CURRENT_RELEASE" "$APP_LINK.restore.$$" || ! mv -Tf -- "$APP_LINK.restore.$$" "$APP_LINK"; then
                    printf 'mir3-web install: recovery failed while restoring app link\n' >&2
                    recovery_status=1
                fi
            else
                if ! rm -f -- "$APP_LINK"; then
                    printf 'mir3-web install: recovery failed while removing app link\n' >&2
                    recovery_status=1
                fi
            fi
        fi
        if (( UNIT_INSTALLED )); then
            if [[ -f "$BACKUP_DIR/mir3-web.service" && ! -L "$BACKUP_DIR/mir3-web.service" ]]; then
                if ! replace_regular_file "$BACKUP_DIR/mir3-web.service" "$UNIT_PATH" 0644; then
                    printf 'mir3-web install: recovery failed while restoring unit file\n' >&2
                    recovery_status=1
                fi
            elif [[ -n "$ORIGINAL_UNIT_LINK_TARGET" ]]; then
                if ! restore_unit_link "$ORIGINAL_UNIT_LINK_TARGET"; then
                    printf 'mir3-web install: recovery failed while restoring linked unit path\n' >&2
                    recovery_status=1
                fi
            else
                if ! rm -f -- "$UNIT_PATH"; then
                    printf 'mir3-web install: recovery failed while removing unit file\n' >&2
                    recovery_status=1
                fi
            fi
            if ! systemctl daemon-reload >/dev/null 2>&1; then
                printf 'mir3-web install: recovery failed during daemon reload\n' >&2
                recovery_status=1
            fi
        fi
        if ! restore_enabled_state; then
            printf 'mir3-web install: recovery failed while restoring systemd enable state\n' >&2
            recovery_status=1
        fi
        if (( SERVICE_WAS_ACTIVE )); then
            if ! systemctl start "$SERVICE_NAME" >/dev/null 2>&1 || ! systemctl is-active --quiet "$SERVICE_NAME"; then
                printf 'mir3-web install: recovery failed while restarting the original active service\n' >&2
                recovery_status=1
            fi
        else
            if ! systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || systemctl is-active --quiet "$SERVICE_NAME"; then
                printf 'mir3-web install: recovery failed while restoring the original inactive service\n' >&2
                recovery_status=1
            fi
        fi
    fi
    if [[ -n "$STAGING_DIR" ]]; then
        if ! rm -rf -- "$STAGING_DIR"; then
            printf 'mir3-web install: recovery failed while removing staging directory\n' >&2
            recovery_status=1
        fi
    fi
    if (( recovery_status )); then
        printf 'mir3-web install: recovery failed; preserving original failure status %s\n' "$status" >&2
    fi
    exit "$status"
}
trap restore_on_failure EXIT

backup_current_state "$BACKUP_DIR" "$CURRENT_RELEASE"
"$DOTNET_COMMAND" publish "$PROJECT_FILE" -c Release --no-restore -o "$STAGING_DIR" --nologo
[[ -f "$STAGING_DIR/Mir3.Web.dll" ]] || die "dotnet publish did not produce Mir3.Web.dll"
chown -R root:root "$STAGING_DIR"
chmod -R u=rwX,go=rX "$STAGING_DIR"
mv -- "$STAGING_DIR" "$RELEASE_DIR"
STAGING_DIR=

if (( SERVICE_WAS_ACTIVE )); then
    systemctl stop "$SERVICE_NAME"
fi

ln -s -- "$RELEASE_DIR" "$APP_LINK.new.$$"
mv -Tf -- "$APP_LINK.new.$$" "$APP_LINK"
LINK_SWITCHED=1
validate_portal_database_path
prepare_database_ownership
run_database_migration
ensure_database_ownership

replace_regular_file "$UNIT_SOURCE" "$UNIT_PATH" 0644 || die "cannot safely install unit file"
UNIT_INSTALLED=1
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl start "$SERVICE_NAME"
SERVICE_STARTED=1
verify_service

LINK_SWITCHED=0
printf 'Mir3 web release activated: %s\n' "$RELEASE_DIR"
printf 'Previous release retained for rollback: %s\n' "${CURRENT_RELEASE:-none}"
