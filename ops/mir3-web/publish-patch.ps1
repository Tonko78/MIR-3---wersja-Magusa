[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Alias('Patch')]
    [string]$PatchDirectory,

    [Parameter(Mandatory = $true)]
    [Alias('Target')]
    [string]$SshTarget,

    [Parameter()]
    [string]$RemoteRoot = '/opt/mir3-web/patch',

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$ReleaseName = (Get-Date -Format 'yyyyMMddHHmmss'),

    [Parameter()]
    [string]$PublicBaseUrl = 'https://example.invalid/patch/',

    [Parameter()]
    [string]$SshCommand = 'ssh',

    [Parameter()]
    [string]$ScpCommand = 'scp',

    [Parameter()]
    [string]$CurlCommand = 'curl',

    [Parameter()]
    [switch]$SkipPublicVerify,

    [Parameter()]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-ToFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-NoReparsePointsInPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = Convert-ToFullPath -Path $Path
    while ($current) {
        $item = $null
        try {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        }
        catch {
            if ($_.CategoryInfo.Category -ne 'ObjectNotFound') {
                throw "Patch publish failed: unable to inspect path component '$current': $($_.Exception.Message)"
            }
        }

        if ($null -ne $item) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Patch publish failed: reparse point is not allowed in path '$current'."
            }
        }

        $parent = Split-Path -LiteralPath $current
        if (-not $parent -or ($parent -eq $current)) {
            break
        }
        $current = $parent
    }
}

function Assert-NoReparsePointsBelow {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootFull = Convert-ToFullPath -Path $Root
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($rootFull)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Patch publish failed: reparse point is not allowed in patch input ($($entry.FullName))."
            }
            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
            }
        }
    }
}

function Write-JsonResult {
    param([Parameter(Mandatory = $true)][object]$Value)
    Write-Output ($Value | ConvertTo-Json -Depth 6 -Compress)
}

$patchFull = Convert-ToFullPath -Path $PatchDirectory
Assert-NoReparsePointsInPath -Path $patchFull

if (-not (Test-Path -LiteralPath $patchFull -PathType Container)) {
    throw "Patch publish failed: patch directory does not exist: $patchFull"
}

Assert-NoReparsePointsBelow -Root $patchFull

$manifestPath = Join-Path $patchFull 'PList.Bin'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Patch publish failed: PList.Bin is missing from $patchFull. Run PatchManager first."
}

$payloadFiles = @(Get-ChildItem -LiteralPath $patchFull -File | Where-Object { $_.Name -like '*.gz' })
$staleFiles = @(Get-ChildItem -LiteralPath $patchFull -File | Where-Object {
        $_.Name -ne 'PList.Bin' -and $_.Name -notlike '*.gz'
    })

if ($staleFiles.Count -gt 0) {
    $names = ($staleFiles | ForEach-Object { $_.Name }) -join ', '
    throw "Patch publish failed: unexpected files in patch directory: $names"
}

if ($RemoteRoot -notmatch '^/[A-Za-z0-9._/-]+$' -or $RemoteRoot.Contains('..')) {
    throw "Patch publish failed: unsafe remote root: $RemoteRoot"
}

$releasePath = "$RemoteRoot/releases/$ReleaseName"

$result = [ordered]@{
    mode           = if ($DryRun) { 'DryRun' } else { 'Publish' }
    release        = $ReleaseName
    remoteRoot     = $RemoteRoot
    releasePath    = $releasePath
    manifestBytes  = (Get-Item -LiteralPath $manifestPath).Length
    payloadCount   = $payloadFiles.Count
    payloadBytes   = ($payloadFiles | Measure-Object -Property Length -Sum).Sum
    publicBaseUrl  = $PublicBaseUrl.TrimEnd('/') + '/'
    published      = $false
    publicVerified = $false
}

if ($DryRun) {
    Write-JsonResult -Value $result
    return
}

# Stage the release in a uniquely named local directory so SHA256SUMS can be
# generated without mutating the operator's PatchManager publish directory.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "mir3-patch-publish-$ReleaseName-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    foreach ($file in $payloadFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $staging $file.Name)
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $staging 'PList.Bin')

    $sumLines = foreach ($file in (Get-ChildItem -LiteralPath $staging -File | Sort-Object Name)) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS') -Value $sumLines -Encoding ascii

    # Refuse to overwrite an existing release: a repeated timestamp or a
    # re-run with the same -ReleaseName must not silently nest the new
    # staging directory inside the previous release.
    & $SshCommand $SshTarget "test ! -e '$releasePath'" 2>&1 | ForEach-Object { Write-Verbose $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "Patch publish failed: release already exists on the server: $releasePath"
    }

    # Upload the complete release before touching the live symlink.
    & $ScpCommand -r -q $staging "${SshTarget}:$releasePath" 2>&1 | ForEach-Object { Write-Verbose $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "Patch publish failed: scp exited with code $LASTEXITCODE."
    }

    # Verify checksums on the server, then switch the live symlink atomically.
    $remoteScript = @"
set -eu
cd '$releasePath'
sha256sum -c SHA256SUMS
ln -sfn 'releases/$ReleaseName' '$RemoteRoot/.current.tmp'
mv -Tf '$RemoteRoot/.current.tmp' '$RemoteRoot/current'
"@
    $remoteOutput = & $SshCommand $SshTarget $remoteScript 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Patch publish failed: remote verification or switch failed: $remoteOutput"
    }

    $result.published = $true

    if (-not $SkipPublicVerify) {
        $manifestUrl = $result.publicBaseUrl + 'PList.Bin'
        & $CurlCommand -fsSI --max-time 30 $manifestUrl | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Patch publish failed: public manifest check failed for $manifestUrl"
        }
        $result.publicVerified = $true
    }

    Write-JsonResult -Value $result
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
