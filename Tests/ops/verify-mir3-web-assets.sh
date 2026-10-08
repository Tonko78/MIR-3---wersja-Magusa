#!/usr/bin/env bash
# Verify the checked-in Mir3 web systemd unit contains the deployment contract.
set -Eeuo pipefail

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
SERVICE_FILE=${1:-"$REPOSITORY_ROOT/ops/mir3-web/mir3-web.service"}

if [[ ! -f "$SERVICE_FILE" ]]; then
    printf 'Missing Mir3 web service unit: %s\n' "$SERVICE_FILE" >&2
    exit 1
fi

required_lines=(
    'User=mir3-web'
    'Group=mir3-web'
    'WorkingDirectory=/opt/mir3-web/app'
    'EnvironmentFile=/etc/mir3-web/mir3-web.env'
    'ExecStart=/opt/dotnet/dotnet /opt/mir3-web/app/Mir3.Web.dll'
    'Restart=on-failure'
    'NoNewPrivileges=true'
    'PrivateTmp=true'
    'ProtectSystem=strict'
    'ReadWritePaths=/var/lib/mir3-web /opt/zircon/account-queue /opt/mir3-web/downloads'
)

for required_line in "${required_lines[@]}"; do
    if ! grep -Fqx -- "$required_line" "$SERVICE_FILE"; then
        printf 'Missing required service setting: %s\n' "$required_line" >&2
        exit 1
    fi
done

printf 'Mir3 web service unit verification passed: %s\n' "$SERVICE_FILE"
