[CmdletBinding()]
param(
    [Parameter()]
    [Alias('Source')]
    [string]$SourcePath,

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')]
    [string]$ServerHost = '127.0.0.1',

    [Parameter()]
    [Alias('Destination')]
    [string]$ArchivePath,

    [Parameter()]
    [string]$ValidateOnly,

    [Parameter()]
    [Alias('StagingDirectory')]
    [string]$StagingPath,

    [Parameter()]
    [string]$WorkRoot,

    [Parameter()]
    [string]$SevenZipPath,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$TestMode,

    [Parameter()]
    [switch]$SkipLaunch,

    [Parameter()]
    [switch]$VerifyLaunch,

    [Parameter()]
    [switch]$TestFailAfterArchivePublish,

    [Parameter()]
    [switch]$KeepStaging,

    [Parameter()]
    [switch]$ForceCleanup,

    [Parameter()]
    [ValidateRange(5, 600)]
    [int]$LaunchTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PublicPort = '7000'
$DefaultArchiveName = 'Mir3-Zircon-Client-2026-09-22.7z'
$StagingMarkerName = '.mir3-client-staging-owner'
$WorkspaceMarkerName = '.mir3-client-workspace-owner'

function Convert-ToFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Write-JsonResult {
    param([Parameter(Mandatory = $true)][object]$Value)
    Write-Output ($Value | ConvertTo-Json -Depth 8 -Compress)
}

function Get-RelativeEntryName {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $relative = $Path.Substring($Root.Length)
    return $relative.TrimStart([char[]]@('\', '/'))
}

function Assert-NoReparsePointsInPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    # Check the path itself and every existing ancestor.  Checking only the
    # descendants of a root would allow a symlinked parent to redirect all
    # subsequent reads and writes outside the intended tree.
    $current = Convert-ToFullPath -Path $Path
    while ($current) {
        $item = $null
        try {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        }
        catch {
            if ($_.CategoryInfo.Category -ne 'ObjectNotFound') {
                throw "Packaging failed: unable to inspect path component '$current': $($_.Exception.Message)"
            }
        }

        if ($null -ne $item) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Packaging failed: reparse point is not allowed in path '$current'."
            }
        }

        $parent = Split-Path -LiteralPath $current
        if (-not $parent -or ($parent -eq $current)) {
            break
        }
        $current = $parent
    }
}

function Assert-NoReparsePoints {
    param([Parameter(Mandatory = $true)][string]$Root)
    $rootFull = Convert-ToFullPath -Path $Root
    Assert-NoReparsePointsInPath -Path $rootFull
    $entries = @(Get-SafeTreeEntries -Root $rootFull)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $relative = Get-RelativeEntryName -Root $rootFull -Path $entry.FullName
            throw "Validation failed: reparse point is not allowed in client input ($relative)."
        }
    }
}

function Get-SafeTreeEntries {
    param([Parameter(Mandatory = $true)][string]$Root)
    $rootFull = Convert-ToFullPath -Path $Root
    Assert-NoReparsePointsInPath -Path $rootFull
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($rootFull)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $relative = Get-RelativeEntryName -Root $rootFull -Path $entry.FullName
                throw "Validation failed: reparse point is not allowed in client input ($relative)."
            }
            Write-Output $entry
            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
            }
        }
    }
}

function Test-TextFile {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)
    $binaryExtensions = @(
        '.7z', '.zip', '.rar', '.exe', '.dll', '.ocx', '.sys', '.dat', '.db', '.pak',
        '.bin', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.webp', '.dmp', '.mdmp',
        '.hdmp', '.dump', '.wav', '.mp3', '.mp4', '.avi', '.mov', '.woff', '.woff2',
        '.ttf', '.otf'
    )
    return ($binaryExtensions -notcontains $File.Extension.ToLowerInvariant())
}

function Test-ForbiddenClientName {
    param([Parameter(Mandatory = $true)][string]$Name)
    return (($Name -like '*.before-*') -or ($Name -like 'backup-ui-*') -or
        ($Name -like 'backup-*') -or ($Name -like '*.bak') -or
        ($Name -like '*.backup'))
}

function Read-InspectedText {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory = $true)][string]$Root
    )
    try {
        if ($File.Length -gt 20MB) {
            throw 'file is larger than the safe text inspection limit'
        }
        $bytes = [System.IO.File]::ReadAllBytes($File.FullName)
        $encoding = [System.Text.Encoding]::UTF8
        $offset = 0
        if (($bytes.Length -ge 2) -and ($bytes[0] -eq 0xFF) -and ($bytes[1] -eq 0xFE)) {
            $encoding = [System.Text.Encoding]::Unicode
            $offset = 2
        }
        elseif (($bytes.Length -ge 2) -and ($bytes[0] -eq 0xFE) -and ($bytes[1] -eq 0xFF)) {
            $encoding = [System.Text.Encoding]::BigEndianUnicode
            $offset = 2
        }
        elseif (($bytes.Length -ge 3) -and ($bytes[0] -eq 0xEF) -and ($bytes[1] -eq 0xBB) -and ($bytes[2] -eq 0xBF)) {
            $offset = 3
        }
        return $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch {
        $relative = Get-RelativeEntryName -Root $Root -Path $File.FullName
        throw "Unable to inspect text file '$relative': $($_.Exception.Message)"
    }
}

function Write-InspectedText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $encoding = [System.Text.Encoding]::UTF8
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        if (($bytes.Length -ge 2) -and ($bytes[0] -eq 0xFF) -and ($bytes[1] -eq 0xFE)) {
            $encoding = [System.Text.Encoding]::Unicode
        }
        elseif (($bytes.Length -ge 2) -and ($bytes[0] -eq 0xFE) -and ($bytes[1] -eq 0xFF)) {
            $encoding = [System.Text.Encoding]::BigEndianUnicode
        }
    }
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Test-MeaningfulConfigValue {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    $normalized = $Value.Trim()
    $normalized = $normalized -replace '\s+[;#].*$', ''
    $normalized = $normalized.Trim()
    if (($normalized.Length -ge 2) -and
        (($normalized.StartsWith('"') -and $normalized.EndsWith('"')) -or
         ($normalized.StartsWith("'") -and $normalized.EndsWith("'")))) {
        $normalized = $normalized.Substring(1, $normalized.Length - 2).Trim()
    }
    return $normalized.Length -gt 0
}

function Get-ConfigAssignmentValues {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$Section
    )
    $escapedKey = [System.Text.RegularExpressions.Regex]::Escape($Key)
    $assignmentPattern = "(?im)^[ \t]*$escapedKey[ \t]*=[ \t]*(?<value>[^\r\n]*)"
    if ($Section) {
        $escapedSection = [System.Text.RegularExpressions.Regex]::Escape($Section)
        $sectionPattern = "(?ims)^[ \t]*\[$escapedSection\][ \t]*\r?\n(?<body>.*?)(?=^[ \t]*\[[^\r\n\]]+\][ \t]*(?:\r?\n|$)|\z)"
        foreach ($sectionMatch in [System.Text.RegularExpressions.Regex]::Matches($Content, $sectionPattern)) {
            foreach ($assignment in [System.Text.RegularExpressions.Regex]::Matches($sectionMatch.Groups['body'].Value, $assignmentPattern)) {
                Write-Output $assignment.Groups['value'].Value
            }
        }
        return
    }
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Content, $assignmentPattern)) {
        Write-Output $match.Groups['value'].Value
    }
}

function Get-TextFiles {
    param([Parameter(Mandatory = $true)][string]$Root)
    $files = @(Get-SafeTreeEntries -Root $Root | Where-Object { -not $_.PSIsContainer })
    foreach ($file in $files) {
        if (Test-TextFile -File $file) {
            Write-Output $file
        }
    }
}

function Get-NetworkSectionInfo {
    param([Parameter(Mandatory = $true)][string]$Content)

    $lines = @([System.Text.RegularExpressions.Regex]::Split($Content, '\r?\n'))
    $headers = New-Object 'System.Collections.Generic.List[object]'
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s*\[(?<name>[^\]\r\n]+)\]\s*$') {
            [void]$headers.Add([pscustomobject]@{
                Index = $index
                Name = $Matches['name']
                Line = $lines[$index]
            })
        }
    }

    $networkHeaders = @($headers | Where-Object { $_.Name.Trim() -ieq 'Network' })
    if ($networkHeaders.Count -gt 1) {
        throw 'Zircon.ini contains duplicate [Network] sections.'
    }
    if ($networkHeaders.Count -eq 0) {
        throw 'Zircon.ini is missing the [Network] section.'
    }

    $header = $networkHeaders[0]
    $nextHeader = @($headers | Where-Object { $_.Index -gt $header.Index } | Select-Object -First 1)
    $end = if ($nextHeader.Count -eq 0) { $lines.Count } else { $nextHeader[0].Index }
    $body = if (($header.Index + 1) -lt $end) { @($lines[($header.Index + 1)..($end - 1)]) } else { @() }
    return [pscustomobject]@{
        Lines = $lines
        HeaderIndex = $header.Index
        HeaderLine = $header.Line
        EndIndex = $end
        Body = $body
    }
}

function Test-CanonicalNetworkConfig {
    param([Parameter(Mandatory = $true)][string]$Content)

    $violations = New-Object 'System.Collections.Generic.List[string]'
    try {
        $section = Get-NetworkSectionInfo -Content $Content
        if ($section.HeaderLine -cne '[Network]') {
            [void]$violations.Add('Zircon.ini must contain the exact [Network] section header')
        }

        $networkCount = 0
        $ipCount = 0
        $portCount = 0
        foreach ($line in $section.Body) {
            if ($line -ceq 'UseNetworkConfig=True') {
                $networkCount++
            }
            elseif ($line -ceq "IPAddress=$ServerHost") {
                $ipCount++
            }
            elseif ($line -ceq 'Port=7000') {
                $portCount++
            }
        }
        if ($networkCount -cne 1) {
            [void]$violations.Add('Zircon.ini must contain exactly UseNetworkConfig=True')
        }
        if ($ipCount -cne 1) {
            [void]$violations.Add("Zircon.ini must contain exactly IPAddress=$ServerHost")
        }
        if ($portCount -cne 1) {
            [void]$violations.Add('Zircon.ini must contain exactly Port=7000')
        }

        foreach ($line in $section.Body) {
            if ($line -match '^\s*(?i:UseNetworkConfig|IPAddress|Port)\s*=') {
                if (($line -cne 'UseNetworkConfig=True') -and
                    ($line -cne "IPAddress=$ServerHost") -and
                    ($line -cne 'Port=7000')) {
                    [void]$violations.Add("Zircon.ini contains a non-canonical network assignment: $line")
                }
            }
        }
    }
    catch {
        [void]$violations.Add($_.Exception.Message)
    }
    return $violations
}

function Normalize-NetworkConfig {
    param([Parameter(Mandatory = $true)][string]$Content)

    $newline = if ($Content.Contains("`r`n")) { "`r`n" } else { "`n" }
    $section = $null
    try {
        $section = Get-NetworkSectionInfo -Content $Content
    }
    catch {
        if ($_.Exception.Message -notlike '*missing the [Network] section*') {
            throw "Packaging failed: $($_.Exception.Message)"
        }
    }

    if ($null -eq $section) {
        $separator = if (($Content.Length -eq 0) -or $Content.EndsWith("`n") -or $Content.EndsWith("`r")) { '' } else { $newline }
        return $Content + $separator + "[Network]$newline" +
            "UseNetworkConfig=True$newline" +
            "IPAddress=$ServerHost$newline" +
            "Port=7000$newline"
    }

    $body = @($section.Body | Where-Object {
        ($_ -notmatch '^\s*(?i:UseNetworkConfig|IPAddress|Port)\s*=')
    })
    $lastContentLine = -1
    for ($index = 0; $index -lt $body.Count; $index++) {
        if ($body[$index] -notmatch '^\s*$') {
            $lastContentLine = $index
        }
    }
    $bodyCore = if ($lastContentLine -ge 0) { @($body[0..$lastContentLine]) } else { @() }
    $trailingBlankLines = if ($lastContentLine -ge 0) {
        if ($lastContentLine -lt ($body.Count - 1)) { @($body[($lastContentLine + 1)..($body.Count - 1)]) } else { @() }
    }
    else {
        @($body)
    }
    $canonicalBody = @($bodyCore + @(
        'UseNetworkConfig=True'
        "IPAddress=$ServerHost"
        'Port=7000'
    ) + $trailingBlankLines)

    $newLines = New-Object 'System.Collections.Generic.List[string]'
    for ($index = 0; $index -lt $section.HeaderIndex; $index++) {
        [void]$newLines.Add($section.Lines[$index])
    }
    [void]$newLines.Add('[Network]')
    foreach ($line in $canonicalBody) {
        [void]$newLines.Add($line)
    }
    for ($index = $section.EndIndex; $index -lt $section.Lines.Count; $index++) {
        [void]$newLines.Add($section.Lines[$index])
    }
    return [string]::Join($newline, $newLines)
}

function Invoke-ClientValidation {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootFull = Convert-ToFullPath -Path $Root
    Assert-NoReparsePoints -Root $rootFull
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Validation failed: staging directory does not exist ($rootFull)."
    }

    $violations = New-Object 'System.Collections.Generic.List[string]'
    $entries = @()
    try {
        $entries = @(Get-SafeTreeEntries -Root $rootFull)
    }
    catch {
        [void]$violations.Add("cannot enumerate staging directory: $($_.Exception.Message)")
    }

    foreach ($entry in $entries) {
        $relative = Get-RelativeEntryName -Root $rootFull -Path $entry.FullName
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            [void]$violations.Add("reparse point: $relative")
            continue
        }

        if ($entry.PSIsContainer) {
            if ($entry.Name -ieq 'Errors') {
                [void]$violations.Add("Errors directory: $relative")
            }
            if (Test-ForbiddenClientName -Name $entry.Name) {
                [void]$violations.Add("backup directory: $relative")
            }
            continue
        }

        if (Test-ForbiddenClientName -Name $entry.Name) {
            [void]$violations.Add("backup file: $relative")
        }
        if (($entry.Name -ieq 'Logs.txt') -or ($entry.Name -ieq 'Chat Logs.txt')) {
            [void]$violations.Add("personal log: $relative")
        }
        if ($entry.Name -like '*.png') {
            [void]$violations.Add("PNG screenshot: $relative")
        }
        if ($entry.Name -match '(?i)\.(dmp|mdmp|hdmp|dump)$') {
            [void]$violations.Add("crash dump: $relative")
        }
    }

    $iniPath = Join-Path $rootFull 'Zircon.ini'
    if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) {
        [void]$violations.Add('Zircon.ini is missing')
    }
    else {
        try {
            $iniFile = Get-Item -LiteralPath $iniPath -Force
            $iniContent = Read-InspectedText -File $iniFile -Root $rootFull
            foreach ($networkViolation in @(Test-CanonicalNetworkConfig -Content $iniContent)) {
                [void]$violations.Add($networkViolation)
            }
        }
        catch {
            [void]$violations.Add($_.Exception.Message)
        }
    }

    try {
        $textFiles = @(Get-TextFiles -Root $rootFull)
        foreach ($file in $textFiles) {
            $content = Read-InspectedText -File $file -Root $rootFull
            foreach ($key in @('RememberedEMail', 'RememberedPassword')) {
                $values = @(Get-ConfigAssignmentValues -Content $content -Key $key)
                foreach ($value in $values) {
                    if (Test-MeaningfulConfigValue -Value $value) {
                        $relative = Get-RelativeEntryName -Root $rootFull -Path $file.FullName
                        [void]$violations.Add("$key has a value: $relative")
                        break
                    }
                }
            }
            if ($content -match '(?im)^[ \t]*RememberDetails[ \t]*=[ \t]*true[ \t]*(?:[;#].*)?$') {
                $relative = Get-RelativeEntryName -Root $rootFull -Path $file.FullName
                [void]$violations.Add("RememberDetails=True: $relative")
            }
        }
    }
    catch {
        [void]$violations.Add($_.Exception.Message)
    }

    if ($violations.Count -gt 0) {
        $summary = ($violations | Select-Object -First 20) -join '; '
        if ($violations.Count -gt 20) {
            $summary += '; additional validation failures omitted'
        }
        throw "Validation failed: $summary"
    }

    return [ordered]@{
        passed = $true
        staging = $rootFull
    }
}

function Remove-PrivateClientFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    Assert-NoReparsePoints -Root $Root
    $entries = @(Get-SafeTreeEntries -Root $Root |
        Sort-Object -Property FullName -Descending)
    foreach ($entry in $entries) {
        $remove = $false
        if ($entry.PSIsContainer) {
            $remove = (($entry.Name -ieq 'Errors') -or
                (Test-ForbiddenClientName -Name $entry.Name))
        }
        else {
            $remove = ((Test-ForbiddenClientName -Name $entry.Name) -or
                ($entry.Name -ieq 'Logs.txt') -or ($entry.Name -ieq 'Chat Logs.txt') -or
                ($entry.Name -like '*.png') -or
                ($entry.Name -match '(?i)\.(dmp|mdmp|hdmp|dump)$'))
        }
        if ($remove) {
            [void](Remove-Item -LiteralPath $entry.FullName -Force -Recurse -ErrorAction Stop)
        }
    }
}

function Clear-RememberedCredentials {
    param([Parameter(Mandatory = $true)][string]$Root)
    foreach ($file in @(Get-TextFiles -Root $Root)) {
        $content = Read-InspectedText -File $file -Root $Root
        $updated = $content
        $updated = [System.Text.RegularExpressions.Regex]::Replace(
            $updated,
            '(?im)^(?<prefix>[ \t]*RememberedEMail[ \t]*=[ \t]*).*$',
            '${prefix}'
        )
        $updated = [System.Text.RegularExpressions.Regex]::Replace(
            $updated,
            '(?im)^(?<prefix>[ \t]*RememberedPassword[ \t]*=[ \t]*).*$',
            '${prefix}'
        )
        $updated = [System.Text.RegularExpressions.Regex]::Replace(
            $updated,
            '(?im)^(?<prefix>[ \t]*RememberDetails[ \t]*=[ \t]*).*$','${prefix}False'
        )
        if ($updated -ne $content) {
            Write-InspectedText -Path $file.FullName -Content $updated
        }
    }
}

function Configure-PublicClient {
    param([Parameter(Mandatory = $true)][string]$Root)
    $iniPath = Join-Path $Root 'Zircon.ini'
    if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) {
        throw 'Packaging failed: Zircon.ini is missing from the client root.'
    }
    $iniFile = Get-Item -LiteralPath $iniPath -Force
    $content = Read-InspectedText -File $iniFile -Root $Root
    $updated = Normalize-NetworkConfig -Content $content
    if ($updated -ne $content) {
        Write-InspectedText -Path $iniPath -Content $updated
    }
}

function Resolve-ExistingSafePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = Convert-ToFullPath -Path $Path
    Assert-NoReparsePointsInPath -Path $full
    $missing = New-Object 'System.Collections.Generic.List[string]'
    $current = $full
    while (-not (Test-Path -LiteralPath $current -PathType Any)) {
        $leaf = [System.IO.Path]::GetFileName($current)
        if (-not $leaf) {
            throw "Packaging failed: unable to resolve path '$full'."
        }
        $missing.Insert(0, $leaf)
        $parent = Split-Path -LiteralPath $current
        if (-not $parent -or ($parent -eq $current)) {
            throw "Packaging failed: unable to resolve path '$full'."
        }
        $current = $parent
    }

    $resolved = (Get-Item -LiteralPath $current -Force -ErrorAction Stop).FullName
    foreach ($segment in $missing) {
        $resolved = Join-Path $resolved $segment
    }
    return (Convert-ToFullPath -Path $resolved)
}

function Assert-SafeStagingPath {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Staging
    )
    $sourceResolved = Resolve-ExistingSafePath -Path $Source
    $stagingResolved = Resolve-ExistingSafePath -Path $Staging
    $sourcePrefix = $sourceResolved.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    $stagingPrefix = $stagingResolved.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    $root = [System.IO.Path]::GetPathRoot($stagingResolved)
    if ($stagingResolved -eq $root) {
        throw 'Packaging failed: refusing to use a filesystem root as staging.'
    }
    if (($stagingResolved -eq $sourceResolved) -or
        $stagingResolved.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $sourceResolved.StartsWith($stagingPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Packaging failed: staging must not contain or be contained by the source directory.'
    }
}

function Assert-PathsSeparated {
    param(
        [Parameter(Mandatory = $true)][string]$First,
        [Parameter(Mandatory = $true)][string]$Second,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $firstResolved = Resolve-ExistingSafePath -Path $First
    $secondResolved = Resolve-ExistingSafePath -Path $Second
    $firstPrefix = $firstResolved.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    $secondPrefix = $secondResolved.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    if (($firstResolved -eq $secondResolved) -or
        $firstResolved.StartsWith($secondPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $secondResolved.StartsWith($firstPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Packaging failed: $Description paths overlap."
    }
}

function Write-OwnershipMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$MarkerName,
        [Parameter(Mandatory = $true)][string]$Kind
    )
    $canonical = Resolve-ExistingSafePath -Path $Directory
    $markerPath = Join-Path $canonical $MarkerName
    $content = @(
        "kind=$Kind",
        "path=$canonical",
        "token=$([Guid]::NewGuid().ToString('N'))"
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($markerPath, $content + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
}

function Test-OwnershipMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$MarkerName,
        [Parameter(Mandatory = $true)][string]$Kind
    )
    $canonical = Resolve-ExistingSafePath -Path $Directory
    $markerPath = Join-Path $canonical $MarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return $false
    }
    $marker = Get-Item -LiteralPath $markerPath -Force -ErrorAction Stop
    if (($marker.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Packaging failed: ownership marker is a reparse point ($markerPath)."
    }
    $content = [System.IO.File]::ReadAllText($marker.FullName, [System.Text.Encoding]::UTF8)
    return (($content -match "(?m)^kind=$([System.Text.RegularExpressions.Regex]::Escape($Kind))\s*$") -and
        ($content -match "(?m)^path=$([System.Text.RegularExpressions.Regex]::Escape($canonical))\s*$") -and
        ($content -match '(?m)^token=[0-9a-fA-F]{32}\s*$'))
}

function Test-GeneratedWorkspacePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$WorkRoot
    )
    $candidate = Resolve-ExistingSafePath -Path $Path
    $workRootResolved = Resolve-ExistingSafePath -Path $WorkRoot
    $workRootPrefix = $workRootResolved.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    $current = $candidate
    while ($current -and ($current.StartsWith($workRootPrefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        if ((Test-Path -LiteralPath $current -PathType Container) -and
            (Test-OwnershipMarker -Directory $current -MarkerName $WorkspaceMarkerName -Kind 'workspace')) {
            return $true
        }
        $parent = Split-Path -LiteralPath $current
        if (-not $parent -or ($parent -eq $current)) {
            break
        }
        $current = $parent
    }
    return $false
}

function Initialize-StagingDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$WorkRoot,
        [Parameter(Mandatory = $true)][bool]$AllowForceCleanup
    )
    $full = Convert-ToFullPath -Path $Path
    Assert-NoReparsePointsInPath -Path $full
    $exists = Test-Path -LiteralPath $full -PathType Any
    if ($exists) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) {
            throw "Packaging failed: staging path exists but is not a directory ($full)."
        }
        Assert-NoReparsePoints -Root $full
        $owned = Test-OwnershipMarker -Directory $full -MarkerName $StagingMarkerName -Kind 'staging'
        if (-not $owned) {
            if (-not $AllowForceCleanup -or (-not (Test-GeneratedWorkspacePath -Path $full -WorkRoot $WorkRoot))) {
                throw "Packaging failed: existing staging path is not owned by this script ($full). Refusing recursive cleanup."
            }
        }
        [void](Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop)
    }
    [void](New-Item -ItemType Directory -Path $full -Force -ErrorAction Stop)
    Assert-NoReparsePointsInPath -Path $full
    Write-OwnershipMarker -Directory $full -MarkerName $StagingMarkerName -Kind 'staging'
    return $true
}

function New-SafeGeneratedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = Convert-ToFullPath -Path $Path
    Assert-NoReparsePointsInPath -Path $full
    if (Test-Path -LiteralPath $full -PathType Any) {
        throw "Packaging failed: generated work directory already exists ($full)."
    }
    [void](New-Item -ItemType Directory -Path $full -Force -ErrorAction Stop)
    Assert-NoReparsePointsInPath -Path $full
    return $full
}

function Remove-SafeGeneratedFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Any)) {
        return
    }
    try {
        Assert-NoReparsePointsInPath -Path $Path
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item.PSIsContainer -or (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'temporary archive is not a regular file'
        }
        [void](Remove-Item -LiteralPath $item.FullName -Force -ErrorAction Stop)
    }
    catch {
        [Console]::Error.WriteLine("WARNING: refusing unsafe cleanup of '$Path': $($_.Exception.Message)")
    }
}

function Remove-SafeGeneratedTree {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Any)) {
        return
    }
    try {
        Assert-NoReparsePointsInPath -Path $Path
        Assert-NoReparsePoints -Root $Path
        [void](Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop)
    }
    catch {
        # Never recursively delete a tree after a reparse point or path race
        # is observed.  Leaving a quarantined temporary tree is safer than
        # following an attacker-controlled link during cleanup.
        [Console]::Error.WriteLine("WARNING: refusing unsafe cleanup of '$Path': $($_.Exception.Message)")
    }
}

function Copy-CleanClient {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $sourceFull = Convert-ToFullPath -Path $Source
    $destinationFull = Convert-ToFullPath -Path $Destination
    Assert-NoReparsePoints -Root $sourceFull
    Assert-NoReparsePointsInPath -Path $destinationFull
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($sourceFull)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $relativeDirectory = Get-RelativeEntryName -Root $sourceFull -Path $directory
        $destinationDirectory = $destinationFull
        if ($relativeDirectory) {
            $destinationDirectory = Join-Path $destinationFull $relativeDirectory
            if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
                [void](New-Item -ItemType Directory -Path $destinationDirectory -Force -ErrorAction Stop)
            }
        }
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            if ($entry.Name -ieq $StagingMarkerName -or $entry.Name -ieq $WorkspaceMarkerName) {
                throw "Packaging failed: reserved ownership marker name is present in client input ($($entry.FullName))."
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Packaging failed: reparse point appeared during client copy ($($entry.FullName))."
            }
            $target = Join-Path $destinationDirectory $entry.Name
            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
            }
            else {
                [void](Copy-Item -LiteralPath $entry.FullName -Destination $target -Force -ErrorAction Stop)
            }
        }
    }
}

function Resolve-SevenZip {
    param([string]$RequestedPath)
    if ($RequestedPath) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "Packaging failed: requested 7-Zip executable was not found ($RequestedPath)."
        }
        return (Convert-ToFullPath -Path $RequestedPath)
    }
    foreach ($name in @('7z.exe', '7z', '7zz.exe', '7zz', '7za.exe', '7za')) {
        $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            if ($command.PSObject.Properties.Name -contains 'Source' -and $command.Source) {
                return $command.Source
            }
            if ($command.PSObject.Properties.Name -contains 'Path' -and $command.Path) {
                return $command.Path
            }
        }
    }
    throw 'Packaging failed: 7-Zip (7z/7zz/7za) is not installed or not on PATH.'
}

function Invoke-SevenZip {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Operation
    )
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $null = & $Executable @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "Packaging failed: 7-Zip $Operation returned exit code $exitCode."
    }
}

function Publish-Archive {
    param(
        [Parameter(Mandatory = $true)][string]$TemporaryArchive,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $TemporaryArchive -PathType Leaf)) {
        throw "Packaging failed: verified temporary archive is missing ($TemporaryArchive)."
    }
    Assert-NoReparsePointsInPath -Path $TemporaryArchive
    Assert-NoReparsePointsInPath -Path $Destination
    if (Test-Path -LiteralPath $Destination -PathType Any) {
        $destinationItem = Get-Item -LiteralPath $Destination -Force -ErrorAction Stop
        if ($destinationItem.PSIsContainer) {
            throw "Packaging failed: archive destination is a directory ($Destination)."
        }
        if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Packaging failed: archive destination is a reparse point ($Destination)."
        }
        [System.IO.File]::Replace($TemporaryArchive, $Destination, $null, $true)
    }
    else {
        [System.IO.File]::Move($TemporaryArchive, $Destination)
    }
}

function Get-ArchiveMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Archive,
        [Parameter(Mandatory = $true)][string]$MetadataDirectory
    )
    if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
        throw "Packaging failed: archive was not created ($Archive)."
    }
    if (-not (Test-Path -LiteralPath $MetadataDirectory -PathType Container)) {
        throw "Packaging failed: metadata directory does not exist ($MetadataDirectory)."
    }
    $file = Get-Item -LiteralPath $Archive -Force
    # Get-FileHash is deliberately calculated after 7z t has passed.
    $hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $metadataRoot = Convert-ToFullPath -Path $MetadataDirectory
    Assert-NoReparsePointsInPath -Path $metadataRoot
    $shaPath = Join-Path $metadataRoot "$($file.Name).sha256"
    $jsonPath = Join-Path $metadataRoot "$($file.Name).json"
    Assert-NoReparsePointsInPath -Path $shaPath
    Assert-NoReparsePointsInPath -Path $jsonPath
    [System.IO.File]::WriteAllText($shaPath, "$hash  $($file.Name)$([Environment]::NewLine)", [System.Text.Encoding]::UTF8)
    $metadata = [ordered]@{
        fileName = $file.Name
        version = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        sizeBytes = [int64]$file.Length
        sha256 = $hash
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    [System.IO.File]::WriteAllText($jsonPath, ($metadata | ConvertTo-Json -Depth 4), [System.Text.Encoding]::UTF8)
    return [ordered]@{
        created = $true
        path = $Archive
        sizeBytes = [int64]$file.Length
        sha256 = $hash
        sha256Path = $shaPath
        metadataPath = $jsonPath
    }
}

function Restore-ReleaseBackup {
    param(
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$BackupDirectory,
        [Parameter(Mandatory = $true)][string[]]$PublishedNames,
        [Parameter(Mandatory = $true)][string[]]$MovedExistingNames
    )

    $rollbackErrors = New-Object 'System.Collections.Generic.List[string]'
    for ($index = $PublishedNames.Count - 1; $index -ge 0; $index--) {
        $name = $PublishedNames[$index]
        $publishedPath = Join-Path $DestinationDirectory $name
        try {
            if (Test-Path -LiteralPath $publishedPath -PathType Any) {
                Assert-NoReparsePointsInPath -Path $publishedPath
                [void](Remove-Item -LiteralPath $publishedPath -Force -ErrorAction Stop)
            }
        }
        catch {
            [void]$rollbackErrors.Add("could not remove published ${name}: $($_.Exception.Message)")
        }
    }
    for ($index = $MovedExistingNames.Count - 1; $index -ge 0; $index--) {
        $name = $MovedExistingNames[$index]
        $backupPath = Join-Path $BackupDirectory $name
        $destinationPath = Join-Path $DestinationDirectory $name
        try {
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                if (Test-Path -LiteralPath $destinationPath -PathType Any) {
                    throw "destination still exists ($destinationPath)"
                }
                [System.IO.File]::Move($backupPath, $destinationPath)
            }
        }
        catch {
            [void]$rollbackErrors.Add("could not restore ${name}: $($_.Exception.Message)")
        }
    }
    try {
        if (Test-Path -LiteralPath $BackupDirectory -PathType Container) {
            Assert-NoReparsePoints -Root $BackupDirectory
            [void](Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction Stop)
        }
    }
    catch {
        [void]$rollbackErrors.Add("could not remove rollback directory: $($_.Exception.Message)")
    }
    if ($rollbackErrors.Count -gt 0) {
        throw ('Packaging failed and release rollback was incomplete: ' + (($rollbackErrors | Select-Object -First 10) -join '; '))
    }
}

function Publish-Release {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][bool]$SimulateFailureAfterArchive
    )

    $releaseRoot = Convert-ToFullPath -Path $ReleaseDirectory
    $destinationFull = Convert-ToFullPath -Path $Destination
    $destinationDirectory = Split-Path -Parent $destinationFull
    $archiveName = [System.IO.Path]::GetFileName($destinationFull)
    $artifactNames = @($archiveName, "$archiveName.sha256", "$archiveName.json")
    Assert-NoReparsePoints -Root $releaseRoot
    Assert-NoReparsePointsInPath -Path $destinationDirectory

    foreach ($name in $artifactNames) {
        $sourcePath = Join-Path $releaseRoot $name
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Packaging failed: temporary release artifact is missing ($sourcePath)."
        }
        Assert-NoReparsePointsInPath -Path $sourcePath
        $destinationPath = Join-Path $destinationDirectory $name
        Assert-NoReparsePointsInPath -Path $destinationPath
        if (Test-Path -LiteralPath $destinationPath -PathType Any) {
            $destinationItem = Get-Item -LiteralPath $destinationPath -Force -ErrorAction Stop
            if ($destinationItem.PSIsContainer) {
                throw "Packaging failed: release destination is a directory ($destinationPath)."
            }
            if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Packaging failed: release destination is a reparse point ($destinationPath)."
            }
        }
    }

    $backupDirectory = Join-Path $destinationDirectory ('.' + $archiveName + '.' + [Guid]::NewGuid().ToString('N') + '.rollback')
    New-SafeGeneratedDirectory -Path $backupDirectory | Out-Null
    $publishedNames = New-Object 'System.Collections.Generic.List[string]'
    $movedExistingNames = New-Object 'System.Collections.Generic.List[string]'
    try {
        foreach ($name in $artifactNames) {
            $destinationPath = Join-Path $destinationDirectory $name
            if (Test-Path -LiteralPath $destinationPath -PathType Any) {
                [System.IO.File]::Move($destinationPath, (Join-Path $backupDirectory $name))
                [void]$movedExistingNames.Add($name)
            }
        }
        foreach ($name in $artifactNames) {
            $sourcePath = Join-Path $releaseRoot $name
            $destinationPath = Join-Path $destinationDirectory $name
            if ($name -eq $archiveName) {
                Publish-Archive -TemporaryArchive $sourcePath -Destination $destinationPath
            }
            else {
                [System.IO.File]::Move($sourcePath, $destinationPath)
            }
            [void]$publishedNames.Add($name)
            if (($name -eq $archiveName) -and $SimulateFailureAfterArchive) {
                throw 'Packaging test failure after archive publication.'
            }
        }
        Assert-NoReparsePointsInPath -Path $destinationFull
        foreach ($name in $artifactNames) {
            if (-not (Test-Path -LiteralPath (Join-Path $destinationDirectory $name) -PathType Leaf)) {
                throw "Packaging failed: published release artifact is missing ($name)."
            }
        }
        Remove-SafeGeneratedTree -Path $backupDirectory
        return [ordered]@{
            path = $destinationFull
            sha256Path = Join-Path $destinationDirectory "$archiveName.sha256"
            metadataPath = Join-Path $destinationDirectory "$archiveName.json"
        }
    }
    catch {
        $publishError = $_.Exception.Message
        try {
            Restore-ReleaseBackup -DestinationDirectory $destinationDirectory `
                -BackupDirectory $backupDirectory -PublishedNames $publishedNames.ToArray() `
                -MovedExistingNames $movedExistingNames.ToArray()
        }
        catch {
            throw "Packaging failed during release publication: $publishError; $($_.Exception.Message)"
        }
        throw "Packaging failed during release publication; previous release restored: $publishError"
    }
}

function Invoke-CleanLaunch {
    param(
        [Parameter(Mandatory = $true)][string]$ExtractedRoot,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $client = @(Get-SafeTreeEntries -Root $ExtractedRoot |
        Where-Object { (-not $_.PSIsContainer) -and ($_.Name -ieq 'Zircon.exe') } |
        Select-Object -First 1)
    if ($client.Count -ne 1) {
        throw 'Clean launch failed: extracted archive does not contain Zircon.exe.'
    }

    $process = $null
    $loginWindowFound = $false
    try {
        $process = Start-Process -FilePath $client[0].FullName -WorkingDirectory $client[0].DirectoryName -PassThru -ErrorAction Stop
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if ($process.HasExited) {
                break
            }
            $process.Refresh()
            # TargetForm uses the Magusa client identity for
            # its title.  The live process and non-zero HWND checks prevent a
            # stale Process object or a dead/non-windowed client from passing.
            if (($process.MainWindowHandle -ne [IntPtr]::Zero) -and
                ($process.MainWindowTitle -match '^Mir3 \u2014 Wersja Magusa(?:\s+-\s+.*)?$')) {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
                if ((-not $process.HasExited) -and
                    ($process.MainWindowHandle -ne [IntPtr]::Zero) -and
                    ($process.MainWindowTitle -match '^Mir3 \u2014 Wersja Magusa(?:\s+-\s+.*)?$')) {
                    $loginWindowFound = $true
                    break
                }
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $loginWindowFound) {
            throw "Clean launch failed: live Mir3 Magusa login scene did not become ready within $TimeoutSeconds seconds."
        }
        return [ordered]@{ passed = $true; loginWindowFound = $true }
    }
    finally {
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) {
                    [void]$process.CloseMainWindow()
                    if (-not $process.WaitForExit(5000)) {
                        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    }
                }
            }
            catch {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

$stageCreatedByScript = $false
$workspaceCreatedByScript = $false
$workspaceRoot = $null
$extractPath = $null
$releaseDirectory = $null
$temporaryArchive = $null
try {
    if ($ValidateOnly) {
        $validation = Invoke-ClientValidation -Root $ValidateOnly
        Write-JsonResult -Value ([ordered]@{
            mode = 'ValidateOnly'
            validation = $validation
        })
        exit 0
    }

    if ($TestMode) {
        $DryRun = $true
    }
    if (-not $ArchivePath) {
        $ArchivePath = Join-Path (Get-Location) $DefaultArchiveName
    }
    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        throw "Packaging failed: SourcePath is required (use -SourcePath)."
    }
    $sourceFull = Convert-ToFullPath -Path $SourcePath
    Assert-NoReparsePointsInPath -Path $sourceFull
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        throw "Packaging failed: source client directory was not found ($sourceFull)."
    }

    if (-not $WorkRoot) {
        $WorkRoot = [System.IO.Path]::GetTempPath()
    }
    Assert-NoReparsePointsInPath -Path $WorkRoot
    [void](New-Item -ItemType Directory -Path $WorkRoot -Force -ErrorAction Stop)
    $workRootResolved = Resolve-ExistingSafePath -Path $WorkRoot
    $workspaceRoot = Join-Path $workRootResolved ("mir3-client-workspace-" + [Guid]::NewGuid().ToString('N'))
    $workspaceRoot = New-SafeGeneratedDirectory -Path $workspaceRoot
    Write-OwnershipMarker -Directory $workspaceRoot -MarkerName $WorkspaceMarkerName -Kind 'workspace'
    $workspaceCreatedByScript = $true
    if (-not $StagingPath) {
        $StagingPath = Join-Path $workspaceRoot 'staging'
    }
    $stageFull = Convert-ToFullPath -Path $StagingPath
    Assert-SafeStagingPath -Source $sourceFull -Staging $stageFull
    $stageCreatedByScript = Initialize-StagingDirectory -Path $stageFull -WorkRoot $workRootResolved -AllowForceCleanup:$ForceCleanup

    Copy-CleanClient -Source $sourceFull -Destination $stageFull
    Remove-PrivateClientFiles -Root $stageFull
    Configure-PublicClient -Root $stageFull
    Clear-RememberedCredentials -Root $stageFull
    $validation = Invoke-ClientValidation -Root $stageFull

    $archiveReport = [ordered]@{
        created = $false
        path = (Convert-ToFullPath -Path $ArchivePath)
        sizeBytes = $null
        sha256 = $null
        sha256Path = $null
        metadataPath = $null
    }
    $launchReport = [ordered]@{
        passed = $false
        skipped = $false
    }

    if (-not $DryRun) {
        $archiveFull = Convert-ToFullPath -Path $ArchivePath
        $archiveParent = Split-Path -Parent $archiveFull
        Assert-PathsSeparated -First $sourceFull -Second $archiveFull -Description 'source and archive'
        Assert-PathsSeparated -First $stageFull -Second $archiveFull -Description 'staging and archive'
        Assert-NoReparsePointsInPath -Path $archiveFull
        [void](New-Item -ItemType Directory -Path $archiveParent -Force -ErrorAction Stop)
        Assert-NoReparsePointsInPath -Path $archiveParent
        $sevenZip = Resolve-SevenZip -RequestedPath $SevenZipPath
        # Never hand an existing destination to 7z a: that would append stale
        # members. Build the complete release in a unique sibling directory;
        # no public archive or sidecar is touched until all checks finish.
        $releaseDirectory = Join-Path $archiveParent ('.' + [System.IO.Path]::GetFileName($archiveFull) + '.' + [Guid]::NewGuid().ToString('N') + '.release')
        $releaseDirectory = New-SafeGeneratedDirectory -Path $releaseDirectory
        $temporaryArchive = Join-Path $releaseDirectory ([System.IO.Path]::GetFileName($archiveFull))
        Assert-NoReparsePointsInPath -Path $temporaryArchive
        if (Test-Path -LiteralPath $temporaryArchive -PathType Any) {
            throw "Packaging failed: generated temporary archive path already exists ($temporaryArchive)."
        }
        # Archive only the sanitized staging contents, never the source directory.
        Invoke-SevenZip -Executable $sevenZip -WorkingDirectory $stageFull `
            -Arguments @('a', '-t7z', '-mx=9', $temporaryArchive, '.', ("-xr!$StagingMarkerName")) -Operation 'create'
        # 7z t checks archive integrity before publication metadata is written.
        Invoke-SevenZip -Executable $sevenZip -WorkingDirectory $stageFull `
            -Arguments @('t', $temporaryArchive) -Operation 'test'
        $archiveReport = Get-ArchiveMetadata -Archive $temporaryArchive -MetadataDirectory $releaseDirectory

        $extractPath = Join-Path $workspaceRoot 'extract'
        New-SafeGeneratedDirectory -Path $extractPath | Out-Null
        Invoke-SevenZip -Executable $sevenZip -WorkingDirectory $workspaceRoot `
            -Arguments @('x', '-y', $temporaryArchive, ("-o" + $extractPath)) -Operation 'extract'
        $null = Invoke-ClientValidation -Root $extractPath
        if (-not $VerifyLaunch -or $SkipLaunch) {
            $launchReport = [ordered]@{ passed = $false; skipped = $true }
        }
        else {
            $launchReport = Invoke-CleanLaunch -ExtractedRoot $extractPath -TimeoutSeconds $LaunchTimeoutSeconds
        }
        $publishedRelease = Publish-Release -ReleaseDirectory $releaseDirectory `
            -Destination $archiveFull -SimulateFailureAfterArchive:$TestFailAfterArchivePublish
        $archiveReport.path = $publishedRelease.path
        $archiveReport.sha256Path = $publishedRelease.sha256Path
        $archiveReport.metadataPath = $publishedRelease.metadataPath
        $temporaryArchive = $null
    }
    else {
        $launchReport = [ordered]@{ passed = $false; skipped = $true }
    }

    $mode = if ($TestMode) { 'TestMode' } elseif ($DryRun) { 'DryRun' } else { 'Package' }
    Write-JsonResult -Value ([ordered]@{
        mode = $mode
        source = $sourceFull
        staging = $stageFull
        validation = $validation
        archive = $archiveReport
        launch = $launchReport
    })
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
    exit 1
}
finally {
    if ($temporaryArchive -and (Test-Path -LiteralPath $temporaryArchive)) {
        Remove-SafeGeneratedFile -Path $temporaryArchive
    }
    if ($stageCreatedByScript -and (-not $KeepStaging) -and $StagingPath) {
        # Keep dry-run staging by default so the sanitized result can be inspected.
        if (-not ($DryRun -or $TestMode)) {
            Remove-SafeGeneratedTree -Path $StagingPath
        }
    }
    if ($extractPath -and (Test-Path -LiteralPath $extractPath) -and (-not $KeepStaging)) {
        Remove-SafeGeneratedTree -Path $extractPath
    }
    if ($releaseDirectory -and (Test-Path -LiteralPath $releaseDirectory) -and (-not $KeepStaging)) {
        Remove-SafeGeneratedTree -Path $releaseDirectory
    }
    if ($workspaceCreatedByScript -and $workspaceRoot -and (-not $KeepStaging) -and
        (-not $DryRun) -and (-not $TestMode) -and (Test-Path -LiteralPath $workspaceRoot)) {
        Remove-SafeGeneratedTree -Path $workspaceRoot
    }
}
