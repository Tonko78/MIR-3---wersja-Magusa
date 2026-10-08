#!/usr/bin/env bash
# Regression tests for Mir3 web release selection and deployment safety.
set -Eeuo pipefail

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
ROLLBACK_SCRIPT=$REPOSITORY_ROOT/ops/mir3-web/rollback.sh
INSTALL_SCRIPT=$REPOSITORY_ROOT/ops/mir3-web/install.sh
TMP_ROOT=$(mktemp -d "${TMPDIR:-/root/.hermes/cache/scratch}/mir3-web-ops-test.XXXXXX")
trap 'rm -rf -- "$TMP_ROOT"' EXIT

fail() {
    printf 'FAIL: %s\n' "$*" >&2
    exit 1
}

assert_eq() {
    local expected=$1
    local actual=$2
    local message=$3
    [[ "$expected" == "$actual" ]] || fail "$message (expected: $expected, actual: $actual)"
}

assert_contains() {
    local needle=$1
    local haystack_file=$2
    local message=$3
    grep -Fq -- "$needle" "$haystack_file" || fail "$message (missing: $needle)"
}

assert_not_contains() {
    local needle=$1
    local haystack_file=$2
    local message=$3
    ! grep -Fq -- "$needle" "$haystack_file" || fail "$message (unexpected: $needle)"
}

make_release() {
    local releases_dir=$1
    local name=$2
    local artifact=${3:-valid}
    mkdir -p -- "$releases_dir/$name"
    if [[ "$artifact" == valid ]]; then
        printf 'published release %s\n' "$name" > "$releases_dir/$name/Mir3.Web.dll"
    fi
}

write_mock_commands() {
    local mock_bin=$1
    mkdir -p -- "$mock_bin"

    printf '%s\n' \
        '#!/usr/bin/env bash' \
        'set -Eeuo pipefail' \
        'state_file=${MIR3_WEB_MOCK_STATE_FILE:?}' \
        'enabled_file=${MIR3_WEB_MOCK_ENABLED_FILE:?}' \
        'state=$(<"$state_file")' \
        'log_event() {' \
        '    if [[ -n "${MIR3_WEB_MOCK_ORDER_FILE:-}" ]]; then printf "%s\\n" "$1" >> "$MIR3_WEB_MOCK_ORDER_FILE"; fi' \
        '}' \
        'case "${1:-}" in' \
        '    is-active)' \
        '        [[ "$state" == active ]]' \
        '        ;;' \
        '    is-enabled)' \
        '        enabled_state=${MIR3_WEB_MOCK_ENABLED_STATE:-$(<"$enabled_file")}' \
        '        if [[ "$enabled_state" != not-found ]]; then printf "%s\\n" "$enabled_state"; fi' \
        '        [[ "$enabled_state" == enabled || "$enabled_state" == enabled-runtime || "$enabled_state" == linked ]]' \
        '        ;;' \
        '    stop)' \
        '        log_event stop' \
        '        printf "%s" inactive > "$state_file"' \
        '        ;;' \
        '    start)' \
        '        count_file=${MIR3_WEB_MOCK_START_COUNT_FILE:-}' \
        '        count=0' \
        '        if [[ -n "$count_file" && -f "$count_file" ]]; then count=$(<"$count_file"); fi' \
        '        ((count += 1))' \
        '        [[ -z "$count_file" ]] || printf "%s" "$count" > "$count_file"' \
        '        if [[ "$state" == active ]]; then log_event start-noop; exit 0; fi' \
        '        if [[ "${MIR3_WEB_MOCK_START_FAIL:-0}" == 1 && ( "${MIR3_WEB_MOCK_FAIL_START_ONCE:-0}" != 1 || "$count" == 1 ) ]]; then' \
        '            if [[ "${MIR3_WEB_MOCK_FAIL_START_AFTER_ACTIVE:-0}" == 1 ]]; then printf "%s" active > "$state_file"; log_event start-failed-active; fi' \
        '            exit 1' \
        '        fi' \
        '        printf "%s" active > "$state_file"' \
        '        log_event start' \
        '        ;;' \
        '    enable)' \
        '        if [[ "${2:-}" == --runtime ]]; then printf enabled-runtime > "$enabled_file"; else printf enabled > "$enabled_file"; fi' \
        '        ;;' \
        '    disable)' \
        '        if [[ "${MIR3_WEB_MOCK_RESTORE_NOT_FOUND:-0}" == 1 ]]; then printf not-found > "$enabled_file"; else printf disabled > "$enabled_file"; fi' \
        '        ;;' \
        '    mask)' \
        '        if [[ "${2:-}" == --runtime ]]; then printf masked-runtime > "$enabled_file"; else printf masked > "$enabled_file"; fi' \
        '        ;;' \
        '    link)' \
        '        printf linked > "$enabled_file"' \
        '        ;;' \
        '    daemon-reload|unmask)' \
        '        ;;' \
        '    *) exit 0 ;;' \
        'esac' > "$mock_bin/systemctl"
    printf '%s\n' \
        '#!/usr/bin/env bash' \
        'printf "LISTEN 0 128 127.0.0.1:%s 0.0.0.0:*\\n" "${MIR3_WEB_PORT:-8088}"' > "$mock_bin/ss"
    printf '%s\n' \
        '#!/usr/bin/env bash' \
        'if [[ "${MIR3_WEB_MOCK_CURL_FAIL:-0}" == 1 ]]; then exit 1; fi' \
        'exit 0' > "$mock_bin/curl"
    printf '%s\n' \
        '#!/usr/bin/env bash' \
        'if [[ "${1:-}" == "--version" ]]; then exit 0; fi' \
        'if [[ "${1:-}" == "publish" ]]; then' \
        '    output=' \
        '    while (($#)); do' \
        '        if [[ "$1" == "-o" ]]; then output=$2; shift 2; else shift; fi' \
        '    done' \
        '    mkdir -p -- "$output"' \
        '    : > "$output/Mir3.Web.dll"' \
        'fi' > "$mock_bin/dotnet"
    printf '%s\n' \
        '#!/usr/bin/env bash' \
        'printf "%q " "$@" >> "${MIR3_WEB_MIGRATION_LOG:?}"' \
        'printf "\\n" >> "${MIR3_WEB_MIGRATION_LOG:?}"' \
        'effective_db=${MIR3_WEB_MIGRATION_DB:?}' \
        'for argument in "$@"; do' \
        '    case "$argument" in' \
        '        --property=EnvironmentFile=*)' \
        '            environment_file=${argument#--property=EnvironmentFile=}' \
        '            while IFS="=" read -r key value || [[ -n "$key" ]]; do' \
        '                if [[ "$key" == ConnectionStrings__Portal ]]; then' \
        '                    [[ "$value" == "Data Source="* ]] || exit 1' \
        '                    effective_db=${value#Data Source=}' \
        '                fi' \
        '            done < "$environment_file"' \
        '            ;;' \
        '    esac' \
        'done' \
        'printf "effective-portal-db=%q\\n" "$effective_db" >> "${MIR3_WEB_MIGRATION_LOG:?}"' \
        'if [[ -e "$effective_db" ]]; then printf "pre-migration-portal-db-owner=%q\\n" "$(stat -c "%U:%G" "$effective_db")" >> "${MIR3_WEB_MIGRATION_LOG:?}"; fi' \
        'mkdir -p -- "$(dirname -- "$effective_db")"' \
        ': > "$effective_db"' \
        ': > "$effective_db-wal"' \
        ': > "$effective_db-shm"' > "$mock_bin/systemd-run"
    chmod +x "$mock_bin"/*
}

configure_common_environment() {
    local case_root=$1
    local web_root=$case_root/web
    mkdir -p -- "$web_root" "$case_root/etc" "$case_root/systemd" "$case_root/tmp"
    printf 'Portal__Test=1\n' > "$case_root/etc/mir3-web.env"
    printf active > "$case_root/systemctl-state"
    printf disabled > "$case_root/systemctl-enabled"
    : > "$case_root/migration.log"

    export PATH="$TMP_ROOT/mock-bin:$PATH"
    export MIR3_WEB_SERVICE_NAME=mir3-web-test
    export MIR3_WEB_SERVICE_USER=root
    export MIR3_WEB_SERVICE_GROUP=root
    export MIR3_WEB_ROOT=$web_root
    export MIR3_WEB_RELEASES_DIR=$web_root/releases
    export MIR3_WEB_BACKUPS_DIR=$web_root/backups
    export MIR3_WEB_APP_LINK=$web_root/app
    export MIR3_WEB_DOWNLOADS_DIR=$case_root/downloads
    export MIR3_WEB_LIB_DIR=$case_root/lib
    export MIR3_WEB_LOCK_DIR=$case_root/lib/verification-locks
    export MIR3_WEB_QUEUE_DIR=$case_root/queue
    export MIR3_WEB_ENV_DIR=$case_root/etc
    export MIR3_WEB_ENV_FILE=$case_root/etc/mir3-web.env
    export MIR3_WEB_SYSTEMD_DIR=$case_root/systemd
    export MIR3_WEB_UNIT_SOURCE=$REPOSITORY_ROOT/ops/mir3-web/mir3-web.service
    export MIR3_WEB_UNIT_PATH=$case_root/systemd/mir3-web-test.service
    export MIR3_WEB_DEPLOYMENT_LOCK_FILE=$case_root/deployment.lock
    export MIR3_WEB_ROLLBACK_TMP_DIR=$case_root/tmp
    export MIR3_WEB_MOCK_STATE_FILE=$case_root/systemctl-state
    export MIR3_WEB_MOCK_ENABLED_FILE=$case_root/systemctl-enabled
    export MIR3_WEB_MIGRATION_LOG=$case_root/migration.log
    export MIR3_WEB_MIGRATION_DB=$case_root/lib/portal.db
    export DOTNET_COMMAND=dotnet
    export MIR3_WEB_PORT=18088
    export MIR3_WEB_HEALTH_URL=http://127.0.0.1:18088/healthz
    export MIR3_WEB_MIN_FREE_BYTES=0
    export MIR3_WEB_VERIFY_RETRIES=1
    unset MIR3_WEB_MOCK_START_FAIL MIR3_WEB_MOCK_FAIL_START_ONCE MIR3_WEB_MOCK_FAIL_START_AFTER_ACTIVE MIR3_WEB_MOCK_START_COUNT_FILE MIR3_WEB_MOCK_CURL_FAIL MIR3_WEB_MOCK_ENABLED_STATE MIR3_WEB_MOCK_ORDER_FILE MIR3_WEB_MOCK_RESTORE_NOT_FOUND
}

run_capture() {
    local output_file=$1
    shift
    if "$@" > "$output_file" 2>&1; then
        RUN_STATUS=0
    else
        RUN_STATUS=$?
    fi
}

run_rollback_selection_test() {
    local case_root=$TMP_ROOT/rollback
    local web_root=$case_root/web
    local releases_dir=$web_root/releases
    local active=20260925120000-2
    local predecessor=20260925120000-1
    local older=20260925115959
    local abandoned=20260925120100
    local invalid=20260925115958
    local outside=$case_root/outside
    local output=$case_root/output.log

    mkdir -p -- "$releases_dir" "$web_root/backups" "$case_root/etc" "$case_root/systemd" "$case_root/tmp" "$outside"
    make_release "$releases_dir" "$active"
    make_release "$releases_dir" "$predecessor"
    make_release "$releases_dir" "$older"
    make_release "$releases_dir" "$abandoned"
    make_release "$releases_dir" "$invalid" invalid
    printf 'outside\n' > "$outside/Mir3.Web.dll"
    ln -s -- "$outside" "$releases_dir/20260925115957"
    ln -s -- "$releases_dir/$active" "$web_root/app"
    printf inactive > "$case_root/systemctl-state"
    printf disabled > "$case_root/systemctl-enabled"

    export PATH="$TMP_ROOT/mock-bin:$PATH"
    export MIR3_WEB_SERVICE_NAME=mir3-web-test
    export MIR3_WEB_ROOT=$web_root
    export MIR3_WEB_RELEASES_DIR=$releases_dir
    export MIR3_WEB_BACKUPS_DIR=$web_root/backups
    export MIR3_WEB_APP_LINK=$web_root/app
    export MIR3_WEB_ENV_DIR=$case_root/etc
    export MIR3_WEB_ENV_FILE=$case_root/etc/mir3-web.env
    export MIR3_WEB_SYSTEMD_DIR=$case_root/systemd
    export MIR3_WEB_UNIT_PATH=$case_root/systemd/mir3-web-test.service
    export MIR3_WEB_DEPLOYMENT_LOCK_FILE=$case_root/deployment.lock
    export MIR3_WEB_ROLLBACK_TMP_DIR=$case_root/tmp
    export MIR3_WEB_MOCK_STATE_FILE=$case_root/systemctl-state
    export MIR3_WEB_MOCK_ENABLED_FILE=$case_root/systemctl-enabled
    export MIR3_WEB_MOCK_ENABLED_STATE=disabled
    export MIR3_WEB_PORT=18088
    export MIR3_WEB_HEALTH_URL=http://127.0.0.1:18088/healthz
    export MIR3_WEB_VERIFY_RETRIES=1

    run_capture "$output" "$ROLLBACK_SCRIPT"
    (( RUN_STATUS == 0 )) || fail "rollback fixture failed: $(<"$output")"
    assert_eq "$releases_dir/$predecessor" "$(readlink -f -- "$web_root/app")" \
        'rollback must choose the newest valid release older than the active suffixed timestamp'
}

run_installer_invalid_link_test() {
    local kind=$1
    local case_root=$TMP_ROOT/install-$kind
    local app_link=$case_root/web/app
    local target
    local output=$case_root/output.log

    configure_common_environment "$case_root"
    case "$kind" in
        broken)
            target=$case_root/missing-release
            ln -s -- "$target" "$app_link"
            ;;
        outside)
            target=$case_root/outside-release
            mkdir -p -- "$target"
            ln -s -- "$target" "$app_link"
            ;;
        path)
            mkdir -p -- "$app_link"
            target=$app_link
            ;;
        *)
            fail "unknown invalid-link fixture: $kind"
            ;;
    esac

    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    (( RUN_STATUS != 0 )) || fail "installer accepted invalid existing app $kind"
    case "$kind" in
        broken) assert_contains 'current app symlink target is missing' "$output" 'broken app symlink must fail closed' ;;
        outside) assert_contains 'current app symlink points outside releases' "$output" 'out-of-tree app symlink must fail closed' ;;
        path) assert_contains 'current app path exists but is not a symlink' "$output" 'existing non-symlink app path must fail closed' ;;
    esac
    if [[ "$kind" != path ]]; then
        assert_eq "$target" "$(readlink -- "$app_link")" "installer must preserve invalid $kind app link"
    else
        [[ -d "$app_link" ]] || fail 'installer must preserve existing app path'
    fi
}

run_installer_absent_link_test() {
    local case_root=$TMP_ROOT/install-absent
    local web_root=$case_root/web
    local output=$case_root/output.log

    configure_common_environment "$case_root"
    export MIR3_WEB_SERVICE_USER=nobody
    export MIR3_WEB_SERVICE_GROUP=nogroup
    mkdir -p -- "$case_root/lib"
    printf existing > "$case_root/lib/portal.db"
    : > "$case_root/lib/portal.db-wal"
    : > "$case_root/lib/portal.db-shm"
    chown root:root "$case_root/lib/portal.db" "$case_root/lib/portal.db-wal" "$case_root/lib/portal.db-shm"
    chmod 0600 "$case_root/lib/portal.db" "$case_root/lib/portal.db-wal" "$case_root/lib/portal.db-shm"
    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    (( RUN_STATUS == 0 )) || fail "installer rejected an absent app link: $(<"$output")"
    [[ -L "$web_root/app" ]] || fail 'first install must create the app symlink'
    local release_target
    release_target=$(readlink -f -- "$web_root/app")
    [[ "$release_target" == "$web_root/releases/"* ]] || fail 'first install app link must target releases'
    [[ -f "$release_target/Mir3.Web.dll" ]] || fail 'first install release must contain Mir3.Web.dll'
    assert_contains '--uid=nobody' "$case_root/migration.log" 'migration must run as the service user'
    assert_contains '--gid=nogroup' "$case_root/migration.log" 'migration must run as the service group'
    assert_contains 'pre-migration-portal-db-owner=nobody:nogroup' "$case_root/migration.log" 'existing database artifacts must be service-owned before migration'
    [[ "$(stat -c '%U:%G' "$case_root/lib/portal.db")" == nobody:nogroup ]] || fail 'portal database ownership must be service ownership'
    [[ "$(stat -c '%U:%G' "$case_root/lib/portal.db-wal")" == nobody:nogroup ]] || fail 'portal database WAL ownership must be service ownership'
    [[ "$(stat -c '%U:%G' "$case_root/lib/portal.db-shm")" == nobody:nogroup ]] || fail 'portal database SHM ownership must be service ownership'
}

run_installer_malicious_database_override_test() {
    local case_root=$TMP_ROOT/install-malicious-database-override
    local outside_db=$case_root/outside/attacker.db
    local output=$case_root/output.log

    configure_common_environment "$case_root"
    mkdir -p -- "$(dirname -- "$outside_db")"
    printf 'ConnectionStrings__Portal=Data Source=%s\n' "$outside_db" > "$case_root/etc/mir3-web.env"

    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    (( RUN_STATUS == 0 )) || fail "installer accepted a malicious database override: $(<"$output")"
    [[ -f "$case_root/lib/portal.db" ]] || fail 'migration must create the configured portal database'
    [[ ! -e "$outside_db" ]] || fail 'migration must not create the database selected by the runtime override'
    assert_contains "--property=EnvironmentFile=$case_root/etc/mir3-web.env" "$case_root/migration.log" \
        'migration must preserve the runtime environment file'
    assert_contains "effective-portal-db=$case_root/lib/portal.db" "$case_root/migration.log" \
        'migration must pin the portal database to the configured path'
}

run_symlinked_release_root_test() {
    local case_root=$TMP_ROOT/install-release-symlink
    local output=$case_root/output.log
    configure_common_environment "$case_root"
    mkdir -p -- "$case_root/outside-releases"
    ln -s -- "$case_root/outside-releases" "$case_root/web/releases"
    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    (( RUN_STATUS != 0 )) || fail 'installer accepted a symlinked release root'
    assert_contains 'unsafe symlink path component' "$output" 'symlinked release root must fail closed'
}

run_rollback_symlinked_release_root_test() {
    local case_root=$TMP_ROOT/rollback-release-symlink
    local output=$case_root/output.log
    configure_common_environment "$case_root"
    mkdir -p -- "$case_root/outside-releases"
    ln -s -- "$case_root/outside-releases" "$case_root/web/releases"
    run_capture "$output" "$ROLLBACK_SCRIPT"
    (( RUN_STATUS != 0 )) || fail 'rollback accepted a symlinked release root'
    assert_contains 'unsafe symlink path component' "$output" 'rollback symlinked release root must fail closed'
}

run_env_symlink_test() {
    local case_root=$TMP_ROOT/install-env-symlink
    local output=$case_root/output.log
    configure_common_environment "$case_root"
    printf 'secret=outside\n' > "$case_root/outside.env"
    rm -f -- "$case_root/etc/mir3-web.env"
    ln -s -- "$case_root/outside.env" "$case_root/etc/mir3-web.env"
    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    (( RUN_STATUS != 0 )) || fail 'installer followed a symlinked environment file'
    assert_contains 'regular non-symlink file' "$output" 'symlinked environment file must fail closed'
    assert_eq 'secret=outside' "$(<"$case_root/outside.env")" 'environment symlink target must remain unchanged'
}

run_installer_state_restore_test() {
    local enabled_state=${1:-disabled}
    local case_root=$TMP_ROOT/install-state-restore-$enabled_state
    local output=$case_root/output.log
    configure_common_environment "$case_root"
    export MIR3_WEB_MOCK_START_FAIL=1
    unset MIR3_WEB_MOCK_ENABLED_STATE
    if [[ "$enabled_state" == not-found ]]; then export MIR3_WEB_MOCK_RESTORE_NOT_FOUND=1; else unset MIR3_WEB_MOCK_RESTORE_NOT_FOUND; fi
    printf '%s' "$enabled_state" > "$case_root/systemctl-enabled"
    printf 'old unit\n' > "$case_root/systemd/mir3-web-test.service"
    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    (( RUN_STATUS != 0 )) || fail 'installer unexpectedly succeeded with forced start failure'
    assert_eq "$enabled_state" "$(<"$case_root/systemctl-enabled")" 'installer must restore the exact previous systemd enable state'
    assert_eq 'old unit' "$(<"$case_root/systemd/mir3-web-test.service")" 'installer must restore the previous unit'
    if [[ "$enabled_state" == not-found ]]; then
        assert_not_contains 'mir3-web install: recovery failed while restoring systemd enable state' "$output" 'installer must accept a real systemd not-found state during recovery'
    fi
}

run_rollback_failure_recovery_test() {
    local enabled_state=${1:-enabled}
    local case_root=$TMP_ROOT/rollback-recovery-$enabled_state
    local web_root=$case_root/web
    local releases_dir=$web_root/releases
    local current=20260925130000
    local previous=20260925125959
    local output=$case_root/output.log
    configure_common_environment "$case_root"
    mkdir -p -- "$releases_dir/$current" "$releases_dir/$previous" "$web_root/backups/$current"
    printf current > "$releases_dir/$current/Mir3.Web.dll"
    printf previous > "$releases_dir/$previous/Mir3.Web.dll"
    ln -s -- "$releases_dir/$current" "$web_root/app"
    printf 'original unit\n' > "$case_root/systemd/mir3-web-test.service"
    printf 'original secret\n' > "$case_root/etc/mir3-web.env"
    printf 'backup unit\n' > "$web_root/backups/$current/mir3-web.service"
    printf 'backup secret\n' > "$web_root/backups/$current/mir3-web.env"
    printf active > "$case_root/systemctl-state"
    printf '%s' "$enabled_state" > "$case_root/systemctl-enabled"
    unset MIR3_WEB_MOCK_ENABLED_STATE
    if [[ "$enabled_state" == not-found ]]; then export MIR3_WEB_MOCK_RESTORE_NOT_FOUND=1; else unset MIR3_WEB_MOCK_RESTORE_NOT_FOUND; fi
    export MIR3_WEB_MOCK_START_FAIL=1
    export MIR3_WEB_MOCK_FAIL_START_ONCE=1
    export MIR3_WEB_MOCK_FAIL_START_AFTER_ACTIVE=1
    export MIR3_WEB_MOCK_START_COUNT_FILE=$case_root/start-count
    export MIR3_WEB_MOCK_ORDER_FILE=$case_root/systemctl-order

    run_capture "$output" "$ROLLBACK_SCRIPT" --restore-unit --restore-env
    (( RUN_STATUS != 0 )) || fail 'rollback unexpectedly succeeded with forced start failure'
    assert_eq "$releases_dir/$current" "$(readlink -f -- "$web_root/app")" 'failed rollback must restore the active app target'
    assert_eq 'original unit' "$(<"$case_root/systemd/mir3-web-test.service")" 'failed rollback must restore the original unit'
    assert_eq 'original secret' "$(<"$case_root/etc/mir3-web.env")" 'failed rollback must restore the original environment'
    assert_eq root:600 "$(stat -c '%U:%a' "$case_root/etc/mir3-web.env")" 'restored environment must remain root-owned and mode 0600'
    assert_eq "$enabled_state" "$(<"$case_root/systemctl-enabled")" 'failed rollback must restore exact enabled state'
    assert_eq active "$(<"$case_root/systemctl-state")" 'failed rollback must restore active state'
    assert_eq $'stop\nstart-failed-active\nstop\nstart' "$(<"$case_root/systemctl-order")" 'failed rollback must stop the failed process before restarting the original service'
    if [[ "$enabled_state" == not-found ]]; then
        assert_not_contains 'mir3-web rollback: recovery failed while restoring systemd enable state' "$output" 'rollback must accept a real systemd not-found state during recovery'
    fi
}

run_lock_contention_test() {
    local case_root=$TMP_ROOT/lock-contention
    local output=$case_root/output.log
    local holder_pid
    local ready_file=$case_root/lock-ready
    configure_common_environment "$case_root"
    (
        exec 9>"$case_root/deployment.lock"
        flock -n 9
        : > "$ready_file"
        sleep 5
    ) &
    holder_pid=$!
    for ((attempt = 0; attempt < 100; attempt++)); do
        [[ -f "$ready_file" ]] && break
        kill -0 "$holder_pid" 2>/dev/null || fail 'lock holder exited before becoming ready'
        sleep 0.01
    done
    [[ -f "$ready_file" ]] || fail 'lock holder did not become ready'
    run_capture "$output" "$INSTALL_SCRIPT" "$REPOSITORY_ROOT"
    kill "$holder_pid" 2>/dev/null || true
    wait "$holder_pid" 2>/dev/null || true
    (( RUN_STATUS != 0 )) || fail 'installer ignored deployment lock contention'
    assert_contains 'another Mir3 web deployment or rollback is already running' "$output" 'lock contention must fail clearly'
}

MOCK_BIN=$TMP_ROOT/mock-bin
write_mock_commands "$MOCK_BIN"
run_rollback_selection_test
run_installer_invalid_link_test broken
run_installer_invalid_link_test outside
run_installer_invalid_link_test path
run_installer_absent_link_test
run_installer_malicious_database_override_test
run_symlinked_release_root_test
run_rollback_symlinked_release_root_test
run_env_symlink_test
run_installer_state_restore_test disabled
run_installer_state_restore_test enabled-runtime
run_installer_state_restore_test linked
run_installer_state_restore_test masked
run_installer_state_restore_test not-found
run_rollback_failure_recovery_test enabled
run_rollback_failure_recovery_test enabled-runtime
run_rollback_failure_recovery_test linked
run_rollback_failure_recovery_test masked
run_rollback_failure_recovery_test disabled
run_rollback_failure_recovery_test not-found
run_lock_contention_test
printf 'Mir3 web deployment regression tests passed\n'
