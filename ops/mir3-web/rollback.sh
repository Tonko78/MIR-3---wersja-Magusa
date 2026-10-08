#!/usr/bin/env bash
# Repoint Mir3 web to the previous release without changing MirDB or Nginx.
set -Eeuo pipefail
umask 027

SERVICE_NAME=${MIR3_WEB_SERVICE_NAME:-mir3-web}
WEB_ROOT=${MIR3_WEB_ROOT:-/opt/mir3-web}
RELEASES_DIR=${MIR3_WEB_RELEASES_DIR:-$WEB_ROOT/releases}
BACKUPS_DIR=${MIR3_WEB_BACKUPS_DIR:-$WEB_ROOT/backups}
APP_LINK=${MIR3_WEB_APP_LINK:-$WEB_ROOT/app}
ENV_DIR=${MIR3_WEB_ENV_DIR:-/etc/mir3-web}
ENV_FILE=${MIR3_WEB_ENV_FILE:-$ENV_DIR/mir3-web.env}
SYSTEMD_DIR=${MIR3_WEB_SYSTEMD_DIR:-/etc/systemd/system}
UNIT_PATH=${MIR3_WEB_UNIT_PATH:-$SYSTEMD_DIR/$SERVICE_NAME.service}
DEPLOYMENT_LOCK_FILE=${MIR3_WEB_DEPLOYMENT_LOCK_FILE:-/run/lock/mir3-web-deployment.lock}
ROLLBACK_TMP_DIR=${MIR3_WEB_ROLLBACK_TMP_DIR:-${TMPDIR:-/tmp}}
PORT=${MIR3_WEB_PORT:-8088}
HEALTH_URL=${MIR3_WEB_HEALTH_URL:-http://127.0.0.1:$PORT/healthz}
VERIFY_RETRIES=${MIR3_WEB_VERIFY_RETRIES:-30}
RESTORE_UNIT=0
RESTORE_ENV=0

usage() {
    printf '%s\n' \
        'Usage: rollback.sh [--restore-unit] [--restore-env]' \
        '' \
        'The previous unit and environment backups are never restored unless their' \
        'corresponding explicit flag is supplied. Environment restore is opt-in because' \
        'it replaces the current runtime configuration, including its secret values.' >&2
}

die() {
    printf 'mir3-web rollback: %s\n' "$*" >&2
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

release_component_less() {
    local left=$1
    local right=$2

    while [[ ${#left} -gt 1 && ${left:0:1} == 0 ]]; do
        left=${left:1}
    done
    while [[ ${#right} -gt 1 && ${right:0:1} == 0 ]]; do
        right=${right:1}
    done
    if [[ ${#left} -ne ${#right} ]]; then
        (( ${#left} < ${#right} ))
    else
        [[ "$left" < "$right" ]]
    fi
}

release_name_is_older() {
    local candidate=$1
    local active=$2
    local candidate_base candidate_suffix active_base active_suffix

    [[ "$candidate" =~ ^([0-9]{14})(-([0-9]+))?$ ]] || return 1
    candidate_base=${BASH_REMATCH[1]}
    candidate_suffix=${BASH_REMATCH[3]:-0}
    [[ "$active" =~ ^([0-9]{14})(-([0-9]+))?$ ]] || return 1
    active_base=${BASH_REMATCH[1]}
    active_suffix=${BASH_REMATCH[3]:-0}

    if [[ "$candidate_base" != "$active_base" ]]; then
        [[ "$candidate_base" < "$active_base" ]]
    else
        release_component_less "$candidate_suffix" "$active_suffix"
    fi
}

select_previous_release() {
    local current_name=$1
    local releases_root candidate candidate_path resolved_candidate artifact_path
    local selected_name= selected_path=

    releases_root=$(readlink -f -- "$RELEASES_DIR") || return 1
    [[ -d "$releases_root" && ! -L "$releases_root" ]] || return 1
    while IFS= read -r candidate; do
        [[ "$candidate" =~ ^[0-9]{14}(-[0-9]+)?$ ]] || continue
        release_name_is_older "$candidate" "$current_name" || continue
        candidate_path=$RELEASES_DIR/$candidate
        [[ -d "$candidate_path" && ! -L "$candidate_path" ]] || continue
        resolved_candidate=$(readlink -f -- "$candidate_path") || continue
        [[ "$resolved_candidate" == "$releases_root/"* ]] || continue
        artifact_path=$resolved_candidate/Mir3.Web.dll
        [[ -f "$artifact_path" && ! -L "$artifact_path" ]] || continue
        if [[ -z "$selected_name" ]] || release_name_is_older "$selected_name" "$candidate"; then
            selected_name=$candidate
            selected_path=$resolved_candidate
        fi
    done < <(find -P "$RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%f\n')

    [[ -n "$selected_path" ]] || return 1
    printf '%s\n' "$selected_path"
}

snapshot_state() {
    chmod 700 "$ROLLBACK_STATE_DIR"
    printf '%s\n' "$CURRENT_RELEASE" > "$ROLLBACK_STATE_DIR/app-target"
    printf '%s\n' "$ORIGINAL_ENABLED_STATE" > "$ROLLBACK_STATE_DIR/enabled-state"
    printf '%s\n' "$ORIGINAL_ACTIVE" > "$ROLLBACK_STATE_DIR/active-state"
    if [[ -L "$UNIT_PATH" ]]; then
        if [[ "$ORIGINAL_ENABLED_STATE" == masked* ]]; then
            [[ "$(readlink -- "$UNIT_PATH")" == /dev/null ]] || die "current unit path must be a regular non-symlink file: $UNIT_PATH"
            printf 'masked\n' > "$ROLLBACK_STATE_DIR/unit-kind"
        elif [[ "$ORIGINAL_ENABLED_STATE" == linked ]]; then
            ORIGINAL_UNIT_LINK_TARGET=$(readlink -- "$UNIT_PATH")
            require_absolute_safe_path "$ORIGINAL_UNIT_LINK_TARGET"
            reject_symlink_components "$(dirname -- "$ORIGINAL_UNIT_LINK_TARGET")"
            [[ -f "$ORIGINAL_UNIT_LINK_TARGET" && ! -L "$ORIGINAL_UNIT_LINK_TARGET" ]] || die "linked systemd unit target must be a regular non-symlink file: $UNIT_PATH"
            printf '%s\n' "$ORIGINAL_UNIT_LINK_TARGET" > "$ROLLBACK_STATE_DIR/unit-link-target"
            printf 'linked\n' > "$ROLLBACK_STATE_DIR/unit-kind"
        else
            die "current unit path must be a regular non-symlink file: $UNIT_PATH"
        fi
        printf '1\n' > "$ROLLBACK_STATE_DIR/unit-present"
    elif [[ -e "$UNIT_PATH" ]]; then
        [[ -f "$UNIT_PATH" ]] || die "current unit path must be a regular non-symlink file: $UNIT_PATH"
        cp -- "$UNIT_PATH" "$ROLLBACK_STATE_DIR/unit"
        printf 'regular\n' > "$ROLLBACK_STATE_DIR/unit-kind"
        printf '1\n' > "$ROLLBACK_STATE_DIR/unit-present"
    else
        printf 'none\n' > "$ROLLBACK_STATE_DIR/unit-kind"
        printf '0\n' > "$ROLLBACK_STATE_DIR/unit-present"
    fi
    if [[ -e "$ENV_FILE" || -L "$ENV_FILE" ]]; then
        [[ -f "$ENV_FILE" && ! -L "$ENV_FILE" ]] || die "runtime environment file must be a regular non-symlink file: $ENV_FILE"
        cp -- "$ENV_FILE" "$ROLLBACK_STATE_DIR/env"
        chmod 600 "$ROLLBACK_STATE_DIR/env"
        printf '1\n' > "$ROLLBACK_STATE_DIR/env-present"
    else
        printf '0\n' > "$ROLLBACK_STATE_DIR/env-present"
    fi
}

replace_regular_file() {
    local source=$1
    local destination=$2
    local mode=$3
    local temporary
    [[ -f "$source" && ! -L "$source" ]] || return 1
    if [[ -L "$destination" ]]; then
        if [[ "$destination" == "$UNIT_PATH" && "$ORIGINAL_ENABLED_STATE" == masked* ]]; then
            [[ "$(readlink -- "$destination")" == /dev/null ]] || return 1
        elif [[ "$destination" == "$UNIT_PATH" && "$ORIGINAL_ENABLED_STATE" == linked ]]; then
            [[ -n "${ORIGINAL_UNIT_LINK_TARGET:-}" && "$(readlink -- "$destination")" == "$ORIGINAL_UNIT_LINK_TARGET" ]] || return 1
        else
            return 1
        fi
    elif [[ -e "$destination" ]]; then
        [[ -f "$destination" ]] || return 1
    fi
    temporary=$(mktemp "$(dirname -- "$destination")/.${SERVICE_NAME}.restore.XXXXXX") || return 1
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

restore_snapshot_file() {
    local source=$1
    local destination=$2
    local mode=$3
    replace_regular_file "$source" "$destination" "$mode"
}

verify_enabled_state() {
    local observed_state
    observed_state=$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)
    observed_state=${observed_state%%$'\n'*}
    [[ -n "$observed_state" ]] || observed_state=not-found
    [[ "$observed_state" == "$ORIGINAL_ENABLED_STATE" ]] || {
        printf 'mir3-web rollback: expected systemd enable state %s, got %s\n' "$ORIGINAL_ENABLED_STATE" "${observed_state:-empty}" >&2
        return 1
    }
}

restore_enabled_state() {
    case "$ORIGINAL_ENABLED_STATE" in
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
            printf 'mir3-web rollback: unsupported systemd enable state: %s\n' "$ORIGINAL_ENABLED_STATE" >&2
            return 1
            ;;
    esac
    verify_enabled_state
}

restore_app_link() {
    local original_target=$1
    if ! ln -s -- "$original_target" "$APP_LINK.restore.$$"; then
        return 1
    fi
    if ! mv -Tf -- "$APP_LINK.restore.$$" "$APP_LINK"; then
        rm -f -- "$APP_LINK.restore.$$"
        return 1
    fi
}

restore_original_state() {
    local original_target restore_status=0
    original_target=$(<"$ROLLBACK_STATE_DIR/app-target")

    # The failed rollback may still have a live process. Stop it before restoring
    # files; otherwise systemctl start can be a no-op on the failed process.
    if ! systemctl stop "$SERVICE_NAME" >/dev/null 2>&1; then
        printf 'mir3-web rollback: recovery failed while stopping failed rollback service\n' >&2
        restore_status=1
    fi
    if ! restore_app_link "$original_target"; then
        printf 'mir3-web rollback: recovery failed while restoring app link\n' >&2
        restore_status=1
    fi
    if [[ $(<"$ROLLBACK_STATE_DIR/unit-kind") == regular ]]; then
        if ! restore_snapshot_file "$ROLLBACK_STATE_DIR/unit" "$UNIT_PATH" 0644; then
            printf 'mir3-web rollback: recovery failed while restoring unit file\n' >&2
            restore_status=1
        fi
    elif [[ $(<"$ROLLBACK_STATE_DIR/unit-kind") == linked ]]; then
        if ! restore_unit_link "$(<"$ROLLBACK_STATE_DIR/unit-link-target")"; then
            printf 'mir3-web rollback: recovery failed while restoring linked unit path\n' >&2
            restore_status=1
        fi
    elif [[ $(<"$ROLLBACK_STATE_DIR/unit-kind") == masked ]]; then
        if [[ -e "$UNIT_PATH" || -L "$UNIT_PATH" ]] && ! rm -f -- "$UNIT_PATH"; then
            printf 'mir3-web rollback: recovery failed while removing masked unit path\n' >&2
            restore_status=1
        fi
    elif [[ -e "$UNIT_PATH" || -L "$UNIT_PATH" ]]; then
        if ! rm -f -- "$UNIT_PATH"; then
            printf 'mir3-web rollback: recovery failed while removing unit file\n' >&2
            restore_status=1
        fi
    fi
    if [[ $(<"$ROLLBACK_STATE_DIR/env-present") == 1 ]]; then
        if ! restore_snapshot_file "$ROLLBACK_STATE_DIR/env" "$ENV_FILE" 0600; then
            printf 'mir3-web rollback: recovery failed while restoring environment file\n' >&2
            restore_status=1
        fi
    elif [[ -e "$ENV_FILE" || -L "$ENV_FILE" ]]; then
        if ! rm -f -- "$ENV_FILE"; then
            printf 'mir3-web rollback: recovery failed while removing environment file\n' >&2
            restore_status=1
        fi
    fi
    if ! systemctl daemon-reload >/dev/null 2>&1; then
        printf 'mir3-web rollback: recovery failed during daemon reload\n' >&2
        restore_status=1
    fi
    if ! restore_enabled_state; then
        printf 'mir3-web rollback: recovery failed while restoring systemd enable state\n' >&2
        restore_status=1
    fi
    if [[ "$ORIGINAL_ACTIVE" == 1 ]]; then
        if ! systemctl start "$SERVICE_NAME" >/dev/null 2>&1; then
            printf 'mir3-web rollback: recovery failed while restarting original active service\n' >&2
            restore_status=1
        elif ! systemctl is-active --quiet "$SERVICE_NAME"; then
            printf 'mir3-web rollback: recovery failed; original service is not active\n' >&2
            restore_status=1
        fi
    else
        if ! systemctl stop "$SERVICE_NAME" >/dev/null 2>&1; then
            printf 'mir3-web rollback: recovery failed while restoring original inactive service\n' >&2
            restore_status=1
        elif systemctl is-active --quiet "$SERVICE_NAME"; then
            printf 'mir3-web rollback: recovery failed; original service is active\n' >&2
            restore_status=1
        fi
    fi
    return "$restore_status"
}

if [[ $# -gt 0 ]]; then
    for argument in "$@"; do
        case "$argument" in
            --restore-unit|--restore-previous-unit)
                RESTORE_UNIT=1
                ;;
            --restore-env|--restore-previous-env)
                RESTORE_ENV=1
                ;;
            --restore-backups)
                RESTORE_UNIT=1
                RESTORE_ENV=1
                ;;
            -h|--help)
                usage
                exit 0
                ;;
            *)
                usage
                exit 2
                ;;
        esac
    done
fi

(( EUID == 0 )) || die "must be run as root"
for configured_path in "$WEB_ROOT" "$RELEASES_DIR" "$BACKUPS_DIR" "$APP_LINK" "$ENV_DIR" "$ENV_FILE" "$SYSTEMD_DIR" "$UNIT_PATH" "$DEPLOYMENT_LOCK_FILE" "$ROLLBACK_TMP_DIR"; do
    require_absolute_safe_path "$configured_path"
done
[[ "$RELEASES_DIR" == "$WEB_ROOT/"* ]] || die "releases directory must be below web root"
[[ "$BACKUPS_DIR" == "$WEB_ROOT/"* ]] || die "backups directory must be below web root"
[[ "$APP_LINK" == "$WEB_ROOT/app" ]] || die "app symlink must be exactly web-root/app"
[[ "$PORT" =~ ^[0-9]+$ && "$PORT" -ge 1 && "$PORT" -le 65535 ]] || die "invalid port: $PORT"
[[ "$VERIFY_RETRIES" =~ ^[1-9][0-9]*$ ]] || die "invalid verification retry count"

require_command chmod
require_command chown
require_command cp
require_command curl
require_command dirname
require_command find
require_command flock
require_command grep
require_command install
require_command ln
require_command mv
require_command readlink
require_command rm
require_command sleep
require_command ss
require_command systemctl
require_command mktemp
reject_symlink_components "$WEB_ROOT"
reject_symlink_components "$RELEASES_DIR"
reject_symlink_components "$BACKUPS_DIR"
reject_symlink_components "$(dirname -- "$APP_LINK")"
reject_symlink_components "$(dirname -- "$ENV_FILE")"
reject_symlink_components "$(dirname -- "$UNIT_PATH")"
reject_symlink_components "$ROLLBACK_TMP_DIR"
validate_directory_layout
acquire_deployment_lock

[[ -L "$APP_LINK" ]] || die "no active app symlink exists: $APP_LINK"
CURRENT_RELEASE=$(readlink -f -- "$APP_LINK") || die "cannot resolve active app symlink"
[[ -d "$CURRENT_RELEASE" ]] || die "active app symlink target is missing: $CURRENT_RELEASE"
RELEASES_ROOT_REAL=$(readlink -f -- "$RELEASES_DIR") || die "cannot canonicalize releases directory"
[[ "$CURRENT_RELEASE" == "$RELEASES_ROOT_REAL/"* ]] || die "active app symlink points outside releases: $CURRENT_RELEASE"
CURRENT_NAME=${CURRENT_RELEASE##*/}
PREVIOUS_RELEASE=$(select_previous_release "$CURRENT_NAME") || die "no previous release is available for rollback"

BACKUP_DIR=$BACKUPS_DIR/$CURRENT_NAME
reject_symlink_components "$BACKUP_DIR"
if (( RESTORE_UNIT )); then
    [[ -f "$BACKUP_DIR/mir3-web.service" && ! -L "$BACKUP_DIR/mir3-web.service" ]] || die "requested unit backup is missing: $BACKUP_DIR/mir3-web.service"
fi
if (( RESTORE_ENV )); then
    [[ -f "$BACKUP_DIR/mir3-web.env" && ! -L "$BACKUP_DIR/mir3-web.env" ]] || die "requested environment backup is missing: $BACKUP_DIR/mir3-web.env"
fi

ORIGINAL_ENABLED_STATE=$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)
[[ -n "$ORIGINAL_ENABLED_STATE" ]] || ORIGINAL_ENABLED_STATE=not-found
ORIGINAL_ENABLED_STATE=${ORIGINAL_ENABLED_STATE%%$'\n'*}
case "$ORIGINAL_ENABLED_STATE" in
    enabled|enabled-runtime|linked|masked|masked-runtime|disabled|not-found) ;;
    *) die "unsupported systemd enable state: $ORIGINAL_ENABLED_STATE" ;;
esac
ORIGINAL_UNIT_LINK_TARGET=
ORIGINAL_ACTIVE=0
if systemctl is-active --quiet "$SERVICE_NAME"; then
    ORIGINAL_ACTIVE=1
fi
ROLLBACK_STATE_DIR=$(mktemp -d "$ROLLBACK_TMP_DIR/mir3-web-rollback.XXXXXX")
SNAPSHOT_READY=0

restore_on_failure() {
    local status=$? recovery_status=0
    trap - EXIT
    if (( status != 0 && SNAPSHOT_READY )); then
        if ! restore_original_state; then
            recovery_status=1
            printf 'mir3-web rollback: recovery failed; preserving original failure status %s\n' "$status" >&2
        fi
    fi
    if ! rm -rf -- "$ROLLBACK_STATE_DIR"; then
        recovery_status=1
        printf 'mir3-web rollback: recovery failed while removing rollback state\n' >&2
    fi
    if (( recovery_status && status == 0 )); then
        exit 1
    fi
    exit "$status"
}
trap restore_on_failure EXIT

snapshot_state
SNAPSHOT_READY=1

systemctl stop "$SERVICE_NAME"

ln -s -- "$PREVIOUS_RELEASE" "$APP_LINK.rollback.$$"
mv -Tf -- "$APP_LINK.rollback.$$" "$APP_LINK"

if (( RESTORE_UNIT )); then
    replace_regular_file "$BACKUP_DIR/mir3-web.service" "$UNIT_PATH" 0644 || die "cannot safely restore unit file"
    systemctl daemon-reload
fi
if (( RESTORE_ENV )); then
    replace_regular_file "$BACKUP_DIR/mir3-web.env" "$ENV_FILE" 0600 || die "cannot safely restore environment file"
    printf 'Restored the previous environment backup by explicit request.\n'
else
    printf 'Environment backup restore was not requested; current environment was left unchanged.\n'
fi

systemctl start "$SERVICE_NAME"
verify_service
printf 'Mir3 web rollback activated: %s\n' "$PREVIOUS_RELEASE"
