param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Restore', 'Verify')]
    [string]$Action,

    [string]$GameRoot = 'Y:\GAME\Fault StP LIGHTKRAVTE',

    [string]$BackupRoot = '',

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$PackageRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$PayloadRoot = Join-Path $PackageRoot 'payload'
$ManifestPath = Join-Path $PackageRoot 'package_manifest.json'
$PathComparison = [StringComparison]::OrdinalIgnoreCase

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $env:LOCALAPPDATA 'CodexGamePatches\FaultStPLIGHTKRAVTE'
}

function Get-FullPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ($full.Equals($root, $PathComparison)) {
        return $root
    }
    return $full.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
}

function Get-PathPrefix([string]$Path) {
    $full = Get-FullPath $Path
    if (
        $full.EndsWith([IO.Path]::DirectorySeparatorChar) -or
        $full.EndsWith([IO.Path]::AltDirectorySeparatorChar)
    ) {
        return $full
    }
    return $full + [IO.Path]::DirectorySeparatorChar
}

function Assert-NoReparseChild(
    [string]$Parent,
    [string]$Child,
    [string]$Description
) {
    $parentFull = Get-FullPath $Parent
    $current = Get-FullPath $Child
    while (-not $current.Equals($parentFull, $PathComparison)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description traverses a reparse point: $current"
            }
        }
        $next = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($next) -or $next -eq $current) {
            throw "$Description could not be bounded under its approved root: $current"
        }
        $current = Get-FullPath $next
    }
}

function Assert-ChildPath(
    [string]$Parent,
    [string]$Child,
    [string]$Description
) {
    $parentFull = Get-FullPath $Parent
    $childFull = Get-FullPath $Child
    $prefix = Get-PathPrefix $parentFull
    if (-not $childFull.StartsWith($prefix, $PathComparison)) {
        throw "$Description escapes its approved root: $childFull"
    }
    Assert-NoReparseChild $parentFull $childFull $Description
}

function Assert-SeparateRoots([string]$First, [string]$Second, [string]$Label) {
    $a = Get-FullPath $First
    $b = Get-FullPath $Second
    $ap = Get-PathPrefix $a
    $bp = Get-PathPrefix $b
    if (
        $a.Equals($b, $PathComparison) -or
        $a.StartsWith($bp, $PathComparison) -or
        $b.StartsWith($ap, $PathComparison)
    ) {
        throw "$Label must be outside both roots: '$a' and '$b'"
    }
}

function Normalize-RelativePath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw 'Manifest contains an empty path.'
    }
    $value = $RelativePath.Replace('/', '\')
    if (
        [IO.Path]::IsPathRooted($value) -or
        $value.Contains(':') -or
        $value.StartsWith('\')
    ) {
        throw "Unsafe manifest path: $RelativePath"
    }
    foreach ($segment in $value.Split('\')) {
        if (
            [string]::IsNullOrWhiteSpace($segment) -or
            $segment -eq '.' -or
            $segment -eq '..' -or
            $segment.EndsWith('.') -or
            $segment.EndsWith(' ') -or
            $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
        ) {
            throw "Unsafe manifest path component: $RelativePath"
        }
        $deviceName = $segment.Split('.')[0].ToUpperInvariant()
        if (
            $deviceName -in @('CON', 'PRN', 'AUX', 'NUL') -or
            $deviceName -match '^(COM|LPT)[1-9]$'
        ) {
            throw "Reserved device name in manifest path: $RelativePath"
        }
    }
    return $value
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Assert-FileHash(
    [string]$Path,
    [long]$ExpectedLength,
    [string]$ExpectedHash,
    [string]$Description
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $ExpectedLength) {
        throw "$Description length mismatch: $Path (expected $ExpectedLength, got $($item.Length))"
    }
    $actual = Get-Sha256 $Path
    if ($actual -ne $ExpectedHash.ToUpperInvariant()) {
        throw "$Description hash mismatch: $Path (expected $ExpectedHash, got $actual)"
    }
}

function Write-JsonAtomic([object]$Value, [string]$Path) {
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $temporary = Join-Path $directory (
        '.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    )
    $replacementBackup = $temporary + '.replace.bak'
    try {
        $Value |
            ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath $temporary -Encoding UTF8
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporary, $Path, $replacementBackup, $true)
        }
        else {
            [IO.File]::Move($temporary, $Path)
        }
    }
    finally {
        foreach ($cleanup in @($temporary, $replacementBackup)) {
            if (Test-Path -LiteralPath $cleanup -PathType Leaf) {
                Remove-Item -LiteralPath $cleanup -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Copy-Atomic(
    [string]$Source,
    [string]$Destination,
    [string]$ExpectedHash
) {
    $directory = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $temporary = Join-Path $directory (
        '.' + [IO.Path]::GetFileName($Destination) + '.' +
        [guid]::NewGuid().ToString('N') + '.lkkr.tmp'
    )
    $replacementBackup = $temporary + '.replace.bak'
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        $actual = Get-Sha256 $temporary
        if ($actual -ne $ExpectedHash.ToUpperInvariant()) {
            throw "Temporary copy hash mismatch: $Destination"
        }
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [IO.File]::Replace($temporary, $Destination, $replacementBackup, $true)
        }
        else {
            [IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        foreach ($cleanup in @($temporary, $replacementBackup)) {
            if (Test-Path -LiteralPath $cleanup -PathType Leaf) {
                Remove-Item -LiteralPath $cleanup -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Assert-GameStopped {
    $process = Get-Process -Name 'LIGHTKRAVTE' -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        throw 'LIGHTKRAVTE.exe is running. Close the game before install or restore.'
    }
}

function Load-PackageManifest {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Package manifest missing: $ManifestPath"
    }
    if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
        throw "Payload directory missing: $PayloadRoot"
    }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.package_id -ne 'kr.cadriaclord.lightkravte.korean') {
        throw 'Unsupported or unexpected package manifest.'
    }
    if ($manifest.files.Count -le 0) {
        throw 'Package manifest has no payload files.'
    }
    return $manifest
}

function Assert-AllowedPayloadPath([string]$RelativePath) {
    $relative = Normalize-RelativePath $RelativePath
    $allowed = (
        $relative -eq 'winhttp.dll' -or
        $relative -eq '.doorstop_version' -or
        $relative -eq 'doorstop_config.ini' -or
        $relative.StartsWith('BepInEx\core\', $PathComparison) -or
        $relative.StartsWith('BepInEx\config\', $PathComparison) -or
        $relative.StartsWith('BepInEx\plugins\LightKravteKorean\', $PathComparison)
    )
    if (-not $allowed) {
        throw "Payload path is outside the patch allowlist: $relative"
    }
    if ($relative.EndsWith('.exe', $PathComparison)) {
        throw "Executable payloads are forbidden: $relative"
    }
    return $relative
}

function Assert-Payload([object]$Manifest) {
    $expected = @{}
    foreach ($entry in $Manifest.files) {
        $relative = Assert-AllowedPayloadPath $entry.path
        if ($expected.ContainsKey($relative)) {
            throw "Duplicate payload path: $relative"
        }
        $expected[$relative] = $entry
        $source = Join-Path $PayloadRoot $relative
        Assert-ChildPath $PayloadRoot $source 'Payload file'
        Assert-FileHash $source ([long]$entry.length) $entry.sha256 'Payload file'
    }

    $actual = @(
        Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File |
            ForEach-Object {
                $_.FullName.Substring((Get-FullPath $PayloadRoot).Length + 1)
            }
    )
    if ($actual.Count -ne $expected.Count) {
        throw "Payload file count mismatch (manifest $($expected.Count), actual $($actual.Count))."
    }
    foreach ($relative in $actual) {
        if (-not $expected.ContainsKey($relative)) {
            throw "Unexpected payload file: $relative"
        }
    }
    [void](Get-RuntimeMutableStateMap $Manifest)
}

function Get-RuntimeMutableStateMap([object]$Manifest) {
    if (
        $Manifest.PSObject.Properties.Name -notcontains
            'runtime_mutable_payload'
    ) {
        throw 'Package manifest is missing runtime_mutable_payload.'
    }

    $payload = @{}
    foreach ($entry in $Manifest.files) {
        $relative = Assert-AllowedPayloadPath $entry.path
        $payload[$relative] = $entry
    }
    $preexisting = @{}
    foreach ($entry in $Manifest.preexisting_payload) {
        $preexisting[(Normalize-RelativePath $entry.path)] = $true
    }

    $map = @{}
    foreach ($entry in @($Manifest.runtime_mutable_payload)) {
        $relative = Assert-AllowedPayloadPath $entry.path
        if ($map.ContainsKey($relative)) {
            throw "Duplicate runtime-mutable payload path: $relative"
        }
        if (-not $payload.ContainsKey($relative)) {
            throw "Runtime-mutable path is not in payload files: $relative"
        }
        if ($preexisting.ContainsKey($relative)) {
            throw "Runtime-mutable payload must be newly added: $relative"
        }

        $states = @($entry.allowed_states)
        if ($states.Count -lt 1) {
            throw "Runtime-mutable payload has no allowed states: $relative"
        }
        $seenStates = @{}
        $includesInstalled = $false
        foreach ($state in $states) {
            $length = [long]$state.length
            $hash = ([string]$state.sha256).ToUpperInvariant()
            if ($length -lt 0 -or $hash -notmatch '^[0-9A-F]{64}$') {
                throw "Invalid runtime-mutable state metadata: $relative"
            }
            $key = "$length`:$hash"
            if ($seenStates.ContainsKey($key)) {
                throw "Duplicate runtime-mutable state: $relative ($key)"
            }
            $seenStates[$key] = $true
            if (
                $length -eq [long]$payload[$relative].length -and
                $hash -eq ([string]$payload[$relative].sha256).ToUpperInvariant()
            ) {
                $includesInstalled = $true
            }
        }
        if (-not $includesInstalled) {
            throw "Runtime-mutable states omit the installed payload: $relative"
        }
        $map[$relative] = $states
    }
    return $map
}

function Test-RuntimeMutableFile(
    [string]$Path,
    [object[]]$AllowedStates
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $length = (Get-Item -LiteralPath $Path).Length
    $hash = Get-Sha256 $Path
    foreach ($state in @($AllowedStates)) {
        if (
            $length -eq [long]$state.length -and
            $hash -eq ([string]$state.sha256).ToUpperInvariant()
        ) {
            return $true
        }
    }
    return $false
}

function Assert-TrustedBaseline([object]$Manifest, [string]$Root) {
    foreach ($entry in $Manifest.trusted_originals) {
        $relative = Normalize-RelativePath $entry.path
        $target = Join-Path $Root $relative
        Assert-ChildPath $Root $target 'Trusted target'
        Assert-FileHash $target ([long]$entry.length) $entry.sha256 'Trusted target'
    }
}

function Assert-Journal(
    [object]$Journal,
    [string]$JournalPath,
    [string]$StateRoot,
    [string]$ExpectedGameRoot,
    [object]$Manifest
) {
    if (
        $Journal.format -ne 1 -or
        $Journal.package_id -ne 'kr.cadriaclord.lightkravte.korean'
    ) {
        throw 'Unsupported or unexpected journal.'
    }
    if (
        $Journal.package_version -ne $Manifest.version -or
        $Journal.package_manifest_sha256 -ne (Get-Sha256 $ManifestPath)
    ) {
        throw 'The active journal belongs to a different package build.'
    }
    if (@('installing', 'installed') -notcontains $Journal.status) {
        throw "Active journal has an invalid state: $($Journal.status)"
    }
    if ((Get-FullPath $Journal.game_root) -ne (Get-FullPath $ExpectedGameRoot)) {
        throw "Active state belongs to another game root: $($Journal.game_root)"
    }

    $backupDirectory = Get-FullPath $Journal.backup_directory
    Assert-ChildPath $StateRoot $backupDirectory 'Journal backup directory'
    if (-not $backupDirectory.Equals(
        (Get-FullPath (Split-Path -Parent $JournalPath)),
        $PathComparison
    )) {
        throw 'Journal backup directory does not match the journal location.'
    }

    $expected = @{}
    foreach ($entry in $Manifest.files) {
        $relative = Assert-AllowedPayloadPath $entry.path
        $expected[$relative] = $entry
    }
    $preexisting = @{}
    foreach ($entry in $Manifest.preexisting_payload) {
        $relative = Normalize-RelativePath $entry.path
        if (-not $expected.ContainsKey($relative)) {
            throw "Pre-existing payload is not present in the payload manifest: $relative"
        }
        $preexisting[$relative] = $entry
    }

    $seen = @{}
    foreach ($state in $Journal.entries) {
        $relative = Assert-AllowedPayloadPath $state.path
        if ($seen.ContainsKey($relative) -or -not $expected.ContainsKey($relative)) {
            throw "Unexpected or duplicate journal entry: $relative"
        }
        $seen[$relative] = $true
        $payload = $expected[$relative]
        if (
            [long]$state.installed_length -ne [long]$payload.length -or
            $state.installed_sha256 -ne $payload.sha256
        ) {
            throw "Journal payload metadata mismatch: $relative"
        }
        if ($preexisting.ContainsKey($relative)) {
            $original = $preexisting[$relative]
            if (
                $state.prior_state -ne 'present' -or
                [long]$state.original_length -ne [long]$original.length -or
                $state.original_sha256 -ne $original.sha256
            ) {
                throw "Journal original metadata mismatch: $relative"
            }
        }
        elseif (
            $state.prior_state -ne 'absent' -or
            [long]$state.original_length -ne 0 -or
            -not [string]::IsNullOrEmpty([string]$state.original_sha256)
        ) {
            throw "Journal incorrectly records an added payload as pre-existing: $relative"
        }
    }
    if ($seen.Count -ne $expected.Count) {
        throw "Journal entry count mismatch (expected $($expected.Count), got $($seen.Count))."
    }

    foreach ($relativeDirectory in @($Journal.created_directories)) {
        $relative = Normalize-RelativePath $relativeDirectory
        $directory = Join-Path $ExpectedGameRoot $relative
        Assert-ChildPath $ExpectedGameRoot $directory 'Journal-created directory'
    }
}

function Read-ActiveState(
    [string]$StateRoot,
    [string]$ExpectedGameRoot,
    [object]$Manifest
) {
    $activePath = Join-Path $StateRoot 'active.json'
    if (-not (Test-Path -LiteralPath $activePath -PathType Leaf)) {
        return $null
    }
    $active = Get-Content -LiteralPath $activePath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if (
        $active.format -ne 1 -or
        $active.package_id -ne 'kr.cadriaclord.lightkravte.korean'
    ) {
        throw 'Unexpected active-state package id.'
    }
    if ((Get-FullPath $active.game_root) -ne (Get-FullPath $ExpectedGameRoot)) {
        throw "Active state belongs to another game root: $($active.game_root)"
    }
    $journalPath = Get-FullPath $active.journal_path
    Assert-ChildPath $StateRoot $journalPath 'Active journal'
    Assert-FileHash $journalPath ([long]$active.journal_length) $active.journal_sha256 'Active journal'
    $journal = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    Assert-Journal $journal $journalPath $StateRoot $ExpectedGameRoot $Manifest
    if ((Get-FullPath $active.game_root) -ne (Get-FullPath $journal.game_root)) {
        throw 'Active-state game root does not match its journal.'
    }
    return $journal
}

function Write-ActiveState(
    [object]$Journal,
    [string]$JournalPath,
    [string]$StateRoot
) {
    $pointer = [ordered]@{
        format = 1
        package_id = 'kr.cadriaclord.lightkravte.korean'
        journal_path = Get-FullPath $JournalPath
        journal_length = (Get-Item -LiteralPath $JournalPath).Length
        journal_sha256 = Get-Sha256 $JournalPath
        game_root = $Journal.game_root
    }
    Write-JsonAtomic $pointer (Join-Path $StateRoot 'active.json')
}

function Verify-LivePayload([object]$Manifest, [string]$Root) {
    $mutable = Get-RuntimeMutableStateMap $Manifest
    foreach ($entry in $Manifest.files) {
        $relative = Normalize-RelativePath $entry.path
        $target = Join-Path $Root $relative
        Assert-ChildPath $Root $target 'Installed payload'
        if ($mutable.ContainsKey($relative)) {
            if (-not (Test-RuntimeMutableFile $target $mutable[$relative])) {
                throw "Installed runtime-mutable payload is invalid: $relative"
            }
        }
        else {
            Assert-FileHash $target ([long]$entry.length) $entry.sha256 'Installed payload'
        }
    }
}

function Get-MissingPayloadDirectories([object]$Manifest, [string]$Root) {
    $directories = @{}
    foreach ($entry in $Manifest.files) {
        $relative = Normalize-RelativePath $entry.path
        $directory = Split-Path -Parent (Join-Path $Root $relative)
        while (
            -not [string]::IsNullOrWhiteSpace($directory) -and
            -not (Get-FullPath $directory).Equals((Get-FullPath $Root), $PathComparison)
        ) {
            Assert-ChildPath $Root $directory 'Payload directory'
            if (Test-Path -LiteralPath $directory) {
                if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
                    throw "Payload parent path is not a directory: $directory"
                }
            }
            else {
                $relativeDirectory = (Get-FullPath $directory).Substring(
                    (Get-FullPath $Root).Length + 1
                )
                $directories[$relativeDirectory] = $true
            }
            $directory = Split-Path -Parent $directory
        }
    }
    return @($directories.Keys | Sort-Object Length)
}

function Remove-CreatedDirectories(
    [object[]]$RelativeDirectories,
    [string]$Root
) {
    foreach ($relative in (@($RelativeDirectories) | Sort-Object Length -Descending)) {
        $directory = Join-Path $Root (Normalize-RelativePath $relative)
        Assert-ChildPath $Root $directory 'Created payload directory'
        if (
            (Test-Path -LiteralPath $directory -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)
        ) {
            Remove-Item -LiteralPath $directory -Force
        }
    }
}

function Install-Patch([object]$Manifest, [string]$Root, [string]$StateRoot) {
    Assert-GameStopped
    $active = Read-ActiveState $StateRoot $Root $Manifest
    if ($null -ne $active) {
        if ((Get-FullPath $active.game_root) -ne (Get-FullPath $Root)) {
            throw "Another game root is recorded as active: $($active.game_root)"
        }
        if ($active.status -eq 'installing') {
            throw 'An interrupted installation is active. Run Restore-KoreanPatch.ps1 before installing again.'
        }
        if (
            $active.package_version -ne $Manifest.version -or
            $active.package_manifest_sha256 -ne (Get-Sha256 $ManifestPath)
        ) {
            throw 'The active installation belongs to a different package build.'
        }
        Verify-LivePayload $Manifest $Root
        return [ordered]@{
            status = 'already_installed'
            game_root = $Root
            backup_directory = $active.backup_directory
            files_verified = $Manifest.files.Count
        }
    }

    Assert-TrustedBaseline $Manifest $Root
    $createdDirectories = @(Get-MissingPayloadDirectories $Manifest $Root)

    $preflight = @()
    foreach ($entry in $Manifest.files) {
        $relative = Normalize-RelativePath $entry.path
        $target = Join-Path $Root $relative
        Assert-ChildPath $Root $target 'Install target'
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $currentHash = Get-Sha256 $target
            $allowed = @(
                $Manifest.preexisting_payload |
                    Where-Object { (Normalize-RelativePath $_.path) -eq $relative }
            )
            if ($allowed.Count -ne 1 -or $currentHash -ne $allowed[0].sha256) {
                throw "Unexpected pre-existing collision: $relative ($currentHash)"
            }
            $preflight += [ordered]@{
                path = $relative
                prior_state = 'present'
                original_length = (Get-Item -LiteralPath $target).Length
                original_sha256 = $currentHash
                installed_length = [long]$entry.length
                installed_sha256 = $entry.sha256.ToUpperInvariant()
            }
        }
        else {
            $preflight += [ordered]@{
                path = $relative
                prior_state = 'absent'
                original_length = 0
                original_sha256 = ''
                installed_length = [long]$entry.length
                installed_sha256 = $entry.sha256.ToUpperInvariant()
            }
        }
    }

    $timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $backupDirectory = Join-Path $StateRoot (
        $timestamp + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    )
    $originalRoot = Join-Path $backupDirectory 'originals'
    $patchedRoot = Join-Path $backupDirectory 'patched'
    New-Item -ItemType Directory -Force -Path $originalRoot, $patchedRoot | Out-Null

    foreach ($state in $preflight) {
        $payloadSource = Join-Path $PayloadRoot $state.path
        $patchedCopy = Join-Path $patchedRoot $state.path
        $patchedDirectory = Split-Path -Parent $patchedCopy
        New-Item -ItemType Directory -Force -Path $patchedDirectory | Out-Null
        Copy-Item -LiteralPath $payloadSource -Destination $patchedCopy
        Assert-FileHash $patchedCopy $state.installed_length $state.installed_sha256 'Patched recovery copy'

        if ($state.prior_state -eq 'present') {
            $live = Join-Path $Root $state.path
            $backup = Join-Path $originalRoot $state.path
            $backupParent = Split-Path -Parent $backup
            New-Item -ItemType Directory -Force -Path $backupParent | Out-Null
            Copy-Item -LiteralPath $live -Destination $backup
            Assert-FileHash $backup $state.original_length $state.original_sha256 'Original backup'
        }
    }

    $journalPath = Join-Path $backupDirectory 'journal.json'
    $journal = [ordered]@{
        format = 1
        package_id = $Manifest.package_id
        package_version = $Manifest.version
        package_manifest_sha256 = Get-Sha256 $ManifestPath
        game_root = Get-FullPath $Root
        backup_directory = Get-FullPath $backupDirectory
        created_utc = [DateTime]::UtcNow.ToString('o')
        status = 'installing'
        applied = @()
        created_directories = $createdDirectories
        entries = $preflight
    }
    Write-JsonAtomic $journal $journalPath
    # Keep the journal referenced by active.json immutable. This guarantees that
    # a crash at any point leaves a hash-valid recovery record.
    Write-ActiveState $journal $journalPath $StateRoot

    $applied = New-Object System.Collections.ArrayList
    try {
        foreach ($state in $preflight) {
            $source = Join-Path $patchedRoot $state.path
            $target = Join-Path $Root $state.path
            Copy-Atomic $source $target $state.installed_sha256
            [void]$applied.Add($state.path)
        }
        Verify-LivePayload $Manifest $Root
        $journal.applied = @($applied)
        $journal.status = 'installed'
        $journal.completed_utc = [DateTime]::UtcNow.ToString('o')
        $installedJournalPath = Join-Path $backupDirectory 'journal.installed.json'
        Write-JsonAtomic $journal $installedJournalPath
        Write-ActiveState $journal $installedJournalPath $StateRoot
    }
    catch {
        $installError = $_
        $rollbackErrors = @()
        foreach ($relative in (@($applied) | Sort-Object -Descending)) {
            $state = $preflight | Where-Object { $_.path -eq $relative }
            $target = Join-Path $Root $relative
            try {
                if ($state.prior_state -eq 'present') {
                    $backup = Join-Path $originalRoot $relative
                    Copy-Atomic $backup $target $state.original_sha256
                }
                elseif (
                    (Test-Path -LiteralPath $target -PathType Leaf) -and
                    (Get-Sha256 $target) -eq $state.installed_sha256
                ) {
                    Remove-Item -LiteralPath $target -Force
                }
            }
            catch {
                $rollbackErrors += "$relative`: $($_.Exception.Message)"
            }
        }
        Remove-CreatedDirectories $createdDirectories $Root
        if ($rollbackErrors.Count) {
            # Leave active.json pointing at the immutable installing journal so
            # Restore can safely retry recovery after the rollback error is fixed.
            throw $installError
        }
        $journal.applied = @($applied)
        $journal.status = 'rolled_back'
        $journal.rollback_errors = @()
        $rolledBackJournalPath = Join-Path $backupDirectory 'journal.rolled_back.json'
        Write-JsonAtomic $journal $rolledBackJournalPath
        $activePath = Join-Path $StateRoot 'active.json'
        if (Test-Path -LiteralPath $activePath -PathType Leaf) {
            Remove-Item -LiteralPath $activePath -Force
        }
        throw $installError
    }

    return [ordered]@{
        status = 'installed'
        game_root = Get-FullPath $Root
        backup_directory = Get-FullPath $backupDirectory
        payload_files = $Manifest.files.Count
        package_version = $Manifest.version
    }
}

function Restore-Patch([object]$Manifest, [string]$Root, [string]$StateRoot) {
    Assert-GameStopped
    $activePath = Join-Path $StateRoot 'active.json'
    $journal = Read-ActiveState $StateRoot $Root $Manifest
    if ($null -eq $journal) {
        throw "No active installation state exists under $StateRoot"
    }
    if ((Get-FullPath $journal.game_root) -ne (Get-FullPath $Root)) {
        throw "Active state belongs to another game root: $($journal.game_root)"
    }
    $backupDirectory = Get-FullPath $journal.backup_directory
    Assert-ChildPath $StateRoot $backupDirectory 'Backup directory'
    $originalRoot = Join-Path $backupDirectory 'originals'
    $patchedRoot = Join-Path $backupDirectory 'patched'
    $mutable = Get-RuntimeMutableStateMap $Manifest

    foreach ($state in $journal.entries) {
        $relative = Normalize-RelativePath $state.path
        $target = Join-Path $Root $relative
        $patched = Join-Path $patchedRoot $relative
        Assert-ChildPath $Root $target 'Restore target'
        Assert-ChildPath $backupDirectory $patched 'Patched recovery copy'
        Assert-FileHash $patched ([long]$state.installed_length) $state.installed_sha256 'Patched recovery copy'
        if ($state.prior_state -eq 'present') {
            $original = Join-Path $originalRoot $relative
            Assert-ChildPath $backupDirectory $original 'Original backup'
            Assert-FileHash $original ([long]$state.original_length) $state.original_sha256 'Original backup'
        }

        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $current = Get-Sha256 $target
            $currentLength = (Get-Item -LiteralPath $target).Length
            $allowedCurrent = (
                $currentLength -eq [long]$state.installed_length -and
                $current -eq $state.installed_sha256
            )
            if ($state.prior_state -eq 'present') {
                $allowedCurrent = (
                    $allowedCurrent -or (
                        $currentLength -eq [long]$state.original_length -and
                        $current -eq $state.original_sha256
                    )
                )
            }
            if (
                -not $allowedCurrent -and
                $mutable.ContainsKey($relative)
            ) {
                $allowedCurrent = Test-RuntimeMutableFile `
                    $target `
                    $mutable[$relative]
            }
            if (-not $allowedCurrent) {
                throw "Refusing restore over drifted file: $relative ($current)"
            }
        }
        elseif ($state.prior_state -eq 'present') {
            throw "Pre-existing file is unexpectedly missing: $relative"
        }
    }

    foreach ($state in $journal.entries) {
        $relative = Normalize-RelativePath $state.path
        $target = Join-Path $Root $relative
        if ($state.prior_state -eq 'present') {
            if ((Get-Sha256 $target) -ne $state.original_sha256) {
                $original = Join-Path $originalRoot $relative
                Copy-Atomic $original $target $state.original_sha256
            }
        }
        elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            $allowedRemoval = (
                (Get-Item -LiteralPath $target).Length -eq
                    [long]$state.installed_length -and
                (Get-Sha256 $target) -eq $state.installed_sha256
            )
            if (
                -not $allowedRemoval -and
                $mutable.ContainsKey($relative)
            ) {
                $allowedRemoval = Test-RuntimeMutableFile `
                    $target `
                    $mutable[$relative]
            }
            if (-not $allowedRemoval) {
                throw "Refusing to remove drifted added file: $relative"
            }
            Remove-Item -LiteralPath $target -Force
        }
    }

    foreach ($state in $journal.entries) {
        $target = Join-Path $Root (Normalize-RelativePath $state.path)
        if ($state.prior_state -eq 'present') {
            Assert-FileHash $target ([long]$state.original_length) $state.original_sha256 'Restored original'
        }
        elseif (Test-Path -LiteralPath $target) {
            throw "Added file remains after restore: $($state.path)"
        }
    }
    Remove-CreatedDirectories @($journal.created_directories) $Root
    # Write the terminal journal at a different path while active.json still
    # points to its immutable, hash-valid recovery journal. Then retire the
    # pointer. Either side of a crash is therefore safely interpretable.
    $journal.status = 'restored'
    $journal |
        Add-Member `
            -NotePropertyName restored_utc `
            -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) `
            -Force
    $journalPath = Join-Path $backupDirectory 'journal.restored.json'
    Write-JsonAtomic $journal $journalPath
    if (Test-Path -LiteralPath $activePath -PathType Leaf) {
        Remove-Item -LiteralPath $activePath -Force
    }

    return [ordered]@{
        status = 'restored'
        game_root = Get-FullPath $Root
        backup_directory = $backupDirectory
        restored_files = $journal.entries.Count
    }
}

$GameRoot = Get-FullPath $GameRoot
$BackupRoot = Get-FullPath $BackupRoot
Assert-SeparateRoots $GameRoot $PackageRoot 'Package root'
Assert-SeparateRoots $GameRoot $BackupRoot 'Backup root'
Assert-SeparateRoots $PackageRoot $BackupRoot 'Backup root'

$manifest = Load-PackageManifest
Assert-Payload $manifest

switch ($Action) {
    'Install' {
        $result = Install-Patch $manifest $GameRoot $BackupRoot
    }
    'Restore' {
        $result = Restore-Patch $manifest $GameRoot $BackupRoot
    }
    'Verify' {
        Assert-GameStopped
        Verify-LivePayload $manifest $GameRoot
        $result = [ordered]@{
            status = 'verified'
            game_root = $GameRoot
            payload_files = $manifest.files.Count
        }
    }
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
}
else {
    $result | Format-List
}
