[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'Claim', 'Stage', 'Deploy', 'Release')]
    [string]$Action,

    [ValidateSet('Primary', 'Secondary', 'Tertiary', 'Quaternary')]
    [string]$Profile = 'Primary',

    [string]$Owner,

    [string]$ExpectedCommit,

    [string]$DabPath,

    [string]$FranthropyPath,

    [switch]$AllowExperimentalBranch,

    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repository
$project = Join-Path $repository 'ComplicatedMarketBoard\ComplicatedMarketBoard.csproj'
$franthropyCommitPath = Join-Path $repository 'Franthropy.commit'
$sourceDirectory = Join-Path $repository 'ComplicatedMarketBoard\bin\Debug'
$sourceDll = Join-Path $sourceDirectory 'ComplicatedMarketBoard.dll'
$profileKey = $Profile.ToLowerInvariant()
$profileDirectoryName = switch ($Profile) {
    'Primary' { 'XIVLauncher' }
    'Secondary' { 'XIVLauncher-Multibox-2' }
    'Tertiary' { 'XIVLauncher-Multibox-3' }
    'Quaternary' { 'XIVLauncher-Multibox-4' }
}
$dabProfile = if ($Profile -eq 'Primary') { 'primary' } else { $profileDirectoryName }
$profileRoot = Join-Path $env:APPDATA $profileDirectoryName
$configPath = Join-Path $profileRoot 'dalamudConfig.json'
$laneRoot = Join-Path $env:LOCALAPPDATA 'FranFkntastic\ComplicatedMarketBoard\dev-lanes'
$leasePath = Join-Path $laneRoot "$profileKey.json"

function Get-RepositoryValue {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $value = & git -C $repository @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }

    return ($value | Out-String).Trim()
}

function Get-ActiveLease {
    if (-not (Test-Path -LiteralPath $leasePath)) {
        return $null
    }

    try {
        $lease = Get-Content -LiteralPath $leasePath -Raw | ConvertFrom-Json
        if ([DateTimeOffset]::Parse($lease.expiresAtUtc) -le [DateTimeOffset]::UtcNow) {
            Remove-Item -LiteralPath $leasePath -Force
            return $null
        }

        return $lease
    }
    catch {
        throw "CMB $Profile lease is unreadable at '$leasePath': $($_.Exception.Message)"
    }
}

function Resolve-DabPath {
    if (-not [string]::IsNullOrWhiteSpace($DabPath)) {
        $resolved = [System.IO.Path]::GetFullPath($DabPath)
    }
    else {
        $resolved = Join-Path $workspace 'DalamudAgentBridge\src\dab\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\dab.exe'
    }

    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "The reviewed DAB executable was not found at '$resolved'. Supply -DabPath explicitly."
    }

    return $resolved
}

function Get-CmbCatalogEntry {
    param([Parameter(Mandatory = $true)][string]$Executable)

    $json = & $Executable plugins --profile $dabProfile | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "DAB could not read the $Profile plugin catalog."
    }

    $catalog = $json | ConvertFrom-Json
    $matches = @($catalog.plugins | Where-Object {
        $_.internalName -eq 'ComplicatedMarketBoard' -and $_.isLoaded -and $_.isDev
    })
    if ($matches.Count -ne 1) {
        throw "$Profile must expose exactly one loaded ComplicatedMarketBoard development entry; found $($matches.Count)."
    }

    return $matches[0]
}

function Get-RegisteredDllPath {
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "$Profile Dalamud configuration was not found at '$configPath'."
    }

    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $locations = @($config.DevPluginLoadLocations.'$values' | Where-Object {
        $_.IsEnabled -and [System.IO.Path]::GetFileName($_.Path) -eq 'ComplicatedMarketBoard.dll'
    })
    if ($locations.Count -ne 1) {
        throw "$Profile must have exactly one enabled ComplicatedMarketBoard development registration; found $($locations.Count)."
    }

    return [System.IO.Path]::GetFullPath($locations[0].Path)
}

function Assert-Owner {
    if ([string]::IsNullOrWhiteSpace($Owner)) {
        throw "-$Action requires a non-empty -Owner contact."
    }
}

function Write-Receipt {
    param([Parameter(Mandatory = $true)][hashtable]$Receipt)
    [pscustomobject]$Receipt | ConvertTo-Json -Depth 6
}

function Remove-TemporaryBuildRoot {
    param([AllowNull()][string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path)) {
        [System.IO.Directory]::Delete([System.IO.Path]::GetFullPath($Path), $true)
    }
}

function Get-RelativeFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$File
    )

    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $filePath = [System.IO.Path]::GetFullPath($File)
    if (-not $filePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "File '$filePath' is outside deployment root '$rootPath'."
    }
    return $filePath.Substring($rootPath.Length)
}

function Copy-DirectoryFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$MainDllName
    )

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    $files = [System.IO.Directory]::EnumerateFiles($Source, '*', [System.IO.SearchOption]::AllDirectories) |
        Sort-Object { if ((Get-RelativeFilePath -Root $Source -File $_) -eq $MainDllName) { 1 } else { 0 } }
    foreach ($file in $files) {
        $relative = Get-RelativeFilePath -Root $Source -File $file
        $target = Join-Path $Destination $relative
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
        [System.IO.File]::Copy($file, $target, $true)
        if ($relative -eq $MainDllName) {
            [System.IO.File]::SetLastWriteTimeUtc($target, [DateTime]::UtcNow)
        }
    }
}

function Restore-DirectoryBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$OriginalFiles
    )

    if (-not (Test-Path -LiteralPath $Backup -PathType Container)) {
        return
    }
    $originalSet = [System.Collections.Generic.HashSet[string]]::new($OriginalFiles, [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in [System.IO.Directory]::EnumerateFiles($Destination, '*', [System.IO.SearchOption]::AllDirectories)) {
        $relative = Get-RelativeFilePath -Root $Destination -File $file
        if (-not $originalSet.Contains($relative)) {
            [System.IO.File]::Delete($file)
        }
    }
    Copy-DirectoryFiles -Source $Backup -Destination $Destination -MainDllName 'ComplicatedMarketBoard.dll'
}

if ($Action -eq 'Status') {
    $entry = $null
    try { $entry = Get-CmbCatalogEntry (Resolve-DabPath) } catch { }
    Write-Receipt @{
        action = 'Status'
        profile = $Profile
        registeredDll = Get-RegisteredDllPath
        lease = Get-ActiveLease
        plugin = $entry
    }
    exit 0
}

Assert-Owner
$activeLease = Get-ActiveLease

if ($Action -eq 'Claim') {
    if ($null -ne $activeLease -and $activeLease.owner -ne $Owner) {
        throw "CMB $Profile is claimed by '$($activeLease.owner)' until $($activeLease.expiresAtUtc)."
    }

    New-Item -ItemType Directory -Path $laneRoot -Force | Out-Null
    $lease = [ordered]@{
        owner = $Owner
        profile = $Profile
        repository = $repository
        claimedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        expiresAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(30).ToString('O')
    }
    $temporaryLease = "$leasePath.$([Guid]::NewGuid().ToString('N')).tmp"
    $lease | ConvertTo-Json | Set-Content -LiteralPath $temporaryLease -Encoding UTF8
    Move-Item -LiteralPath $temporaryLease -Destination $leasePath -Force
    Write-Receipt @{ action = 'Claim'; profile = $Profile; lease = $lease }
    exit 0
}

if ($null -eq $activeLease -or $activeLease.owner -ne $Owner) {
    throw "CMB $Profile is not claimed by '$Owner'. Run -Action Claim first."
}

if ($Action -eq 'Release') {
    Remove-Item -LiteralPath $leasePath -Force
    Write-Receipt @{ action = 'Release'; profile = $Profile; owner = $Owner; released = $true }
    exit 0
}

$branch = Get-RepositoryValue @('branch', '--show-current')
$commit = Get-RepositoryValue @('rev-parse', 'HEAD')
$originCommit = Get-RepositoryValue @('rev-parse', 'origin/master')
$dirty = Get-RepositoryValue @('status', '--porcelain=v1', '--untracked-files=all')
if (-not [string]::IsNullOrWhiteSpace($dirty)) {
    throw "CMB deployment requires a clean worktree."
}
if ($AllowExperimentalBranch) {
    if ([string]::IsNullOrWhiteSpace($ExpectedCommit) -or -not $commit.StartsWith($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Experimental CMB deployment requires -ExpectedCommit matching '$commit'."
    }
    $publishedRefs = Get-RepositoryValue @('branch', '-r', '--contains', $commit)
    if ([string]::IsNullOrWhiteSpace($publishedRefs)) {
        throw "Experimental CMB commit '$commit' is not published on an origin branch."
    }
} else {
    if ($branch -ne 'master') {
        throw "CMB deployment requires branch 'master'; current branch is '$branch'."
    }
    if ($commit -ne $originCommit) {
        throw "CMB master '$commit' does not match origin/master '$originCommit'."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and -not $commit.StartsWith($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CMB master '$commit' does not match expected commit '$ExpectedCommit'."
    }
}

$franthropy = if ([string]::IsNullOrWhiteSpace($FranthropyPath)) {
    Join-Path $workspace 'Franthropy'
} else {
    [System.IO.Path]::GetFullPath($FranthropyPath)
}
$requiredFranthropyCommit = if (Test-Path -LiteralPath $franthropyCommitPath -PathType Leaf) {
    (Get-Content -LiteralPath $franthropyCommitPath -Raw).Trim()
}
else {
    throw "CMB's Franthropy consumer receipt is missing at '$franthropyCommitPath'."
}
if ($requiredFranthropyCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "CMB's Franthropy consumer receipt is not a full Git commit."
}
$franthropyDirty = (& git -C $franthropy status --porcelain=v1 --untracked-files=all | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace($franthropyDirty)) {
    throw "Franthropy must be a clean sibling checkout before CMB deployment."
}
if ($AllowExperimentalBranch) {
    $franthropyHead = (& git -C $franthropy rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $franthropyHead -ne $requiredFranthropyCommit) {
        throw "Experimental Franthropy checkout must equal CMB's required revision '$requiredFranthropyCommit'."
    }
    $publishedFranthropyRefs = (& git -C $franthropy branch -r --contains $requiredFranthropyCommit | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($publishedFranthropyRefs)) {
        throw "Experimental Franthropy revision '$requiredFranthropyCommit' is not published on an origin branch."
    }
} else {
    & git -C $franthropy merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) {
        throw "Franthropy HEAD is not published on origin/main."
    }
    & git -C $franthropy merge-base --is-ancestor $requiredFranthropyCommit HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Franthropy HEAD does not contain CMB's required revision '$requiredFranthropyCommit'."
    }
}

if ($Action -eq 'Stage') {
    $stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) "cmb-stage-$([Guid]::NewGuid().ToString('N'))"
    $stageOutput = Join-Path $stageRoot 'output'
    $stageTarget = Join-Path $profileRoot 'devPlugins\ComplicatedMarketBoard'
    try {
        $stageArguments = @(
            'build', $project, '-c', 'Debug', '--no-restore', '--no-incremental',
            "-p:OutputPath=$stageOutput",
            "-p:FranthropyDalamudProject=$(Join-Path $franthropy 'src\Franthropy.Dalamud\Franthropy.Dalamud.csproj')"
        )
        & dotnet @stageArguments
        if ($LASTEXITCODE -ne 0) { throw "CMB Debug staging build failed with exit code $LASTEXITCODE." }
        $stageDll = Join-Path $stageOutput 'ComplicatedMarketBoard.dll'
        $stageManifest = Join-Path $stageOutput 'ComplicatedMarketBoard.json'
        if (-not (Test-Path -LiteralPath $stageDll) -or -not (Test-Path -LiteralPath $stageManifest)) {
            throw 'CMB staging output is incomplete.'
        }
        $stageHash = (Get-FileHash -LiteralPath $stageDll -Algorithm SHA256).Hash
        Copy-DirectoryFiles -Source $stageOutput -Destination $stageTarget -MainDllName 'ComplicatedMarketBoard.dll'
        $installedDll = Join-Path $stageTarget 'ComplicatedMarketBoard.dll'
        $installedHash = (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash
        if ($installedHash -ne $stageHash) { throw 'CMB staged target hash does not match the build.' }
        Write-Receipt @{
            action = 'Stage'
            profile = $Profile
            branch = $branch
            commit = $commit
            franthropyCommit = $requiredFranthropyCommit
            targetDll = $installedDll
            dllSha256 = $installedHash
        }
    }
    finally {
        Remove-TemporaryBuildRoot $stageRoot
    }
    exit 0
}

$registeredDll = Get-RegisteredDllPath
$expectedRegisteredDll = if ($Profile -eq 'Primary') {
    [System.IO.Path]::GetFullPath($sourceDll)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $profileRoot 'devPlugins\ComplicatedMarketBoard\ComplicatedMarketBoard.dll'))
}
if (-not [string]::Equals($expectedRegisteredDll, $registeredDll, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Profile watches '$registeredDll', not its canonical CMB path '$expectedRegisteredDll'."
}

$dab = Resolve-DabPath
$before = Get-CmbCatalogEntry $dab
if (-not $before.isLoaded -or -not $before.isDev) {
    throw "$Profile ComplicatedMarketBoard must be a loaded development plugin before deployment."
}

$buildDirectory = $sourceDirectory
$temporaryBuildRoot = $null
if ($Profile -ne 'Primary') {
    $temporaryBuildRoot = Join-Path ([System.IO.Path]::GetTempPath()) "cmb-staged-$([Guid]::NewGuid().ToString('N'))"
    $buildDirectory = Join-Path $temporaryBuildRoot 'output'
}

$buildArguments = @(
    'build', $project, '-c', 'Debug', '--no-restore', '--no-incremental',
    "-p:FranthropyDalamudProject=$(Join-Path $franthropy 'src\Franthropy.Dalamud\Franthropy.Dalamud.csproj')"
)
if ($Profile -ne 'Primary') {
    $buildArguments += "-p:OutputPath=$buildDirectory"
}
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    Remove-TemporaryBuildRoot $temporaryBuildRoot
    throw "CMB Debug build failed with exit code $LASTEXITCODE."
}
$buildDll = Join-Path $buildDirectory 'ComplicatedMarketBoard.dll'
if (-not (Test-Path -LiteralPath $buildDll -PathType Leaf)) {
    Remove-TemporaryBuildRoot $temporaryBuildRoot
    throw "CMB build did not produce '$buildDll'."
}

$sourceHash = (Get-FileHash -LiteralPath $buildDll -Algorithm SHA256).Hash
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($buildDll).Version.ToString()
$productVersion = (Get-Item -LiteralPath $buildDll).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or $productVersion -notlike "*$commit*") {
    Remove-TemporaryBuildRoot $temporaryBuildRoot
    throw "CMB artifact product version '$productVersion' does not carry integration commit '$commit'."
}
$deploymentReceipt = $null
$deploymentBackupRoot = $null
$deploymentTargetDirectory = $null
$deploymentOriginalFiles = @()
if ($Profile -ne 'Primary') {
    try {
        $deploymentTargetDirectory = [System.IO.Path]::GetDirectoryName($registeredDll)
        $deploymentBackupRoot = Join-Path ([System.IO.Path]::GetTempPath()) "cmb-staged-backup-$([Guid]::NewGuid().ToString('N'))"
        [System.IO.Directory]::CreateDirectory($deploymentBackupRoot) | Out-Null
        if (Test-Path -LiteralPath $deploymentTargetDirectory -PathType Container) {
            $deploymentOriginalFiles = @([System.IO.Directory]::EnumerateFiles(
                $deploymentTargetDirectory,
                '*',
                [System.IO.SearchOption]::AllDirectories) | ForEach-Object {
                    Get-RelativeFilePath -Root $deploymentTargetDirectory -File $_
                })
            Copy-DirectoryFiles -Source $deploymentTargetDirectory -Destination $deploymentBackupRoot
        }
        Copy-DirectoryFiles -Source $buildDirectory -Destination $deploymentTargetDirectory -MainDllName 'ComplicatedMarketBoard.dll'
        $installedHash = (Get-FileHash -LiteralPath $registeredDll -Algorithm SHA256).Hash
        if (-not [string]::Equals($installedHash, $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Profile did not verify the expected CMB DLL hash after deployment."
        }
        $deploymentReceipt = [ordered]@{
            targetDirectory = $deploymentTargetDirectory
            installedMainDllSha256 = $installedHash
            backupDirectory = $deploymentBackupRoot
        }
    }
    catch {
        Restore-DirectoryBackup -Backup $deploymentBackupRoot -Destination $deploymentTargetDirectory -OriginalFiles $deploymentOriginalFiles
        Remove-TemporaryBuildRoot $deploymentBackupRoot
        throw
    }
    finally {
        Remove-TemporaryBuildRoot $temporaryBuildRoot
    }
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$after = $null
do {
    Start-Sleep -Milliseconds 200
    try {
        $candidate = Get-CmbCatalogEntry $dab
        if ($candidate.isLoaded -and $candidate.isDev -and
            $candidate.runtimeInstanceId -ne $before.runtimeInstanceId -and
            $candidate.version -eq $assemblyVersion) {
            $after = $candidate
            break
        }
    }
    catch {
        # The catalog can disappear briefly while Dalamud replaces the assembly.
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

if ($null -eq $after) {
    if ($Profile -ne 'Primary') {
        Restore-DirectoryBackup -Backup $deploymentBackupRoot -Destination $deploymentTargetDirectory -OriginalFiles $deploymentOriginalFiles
        Remove-TemporaryBuildRoot $deploymentBackupRoot
    }
    throw "$Profile did not advertise the expected CMB commit '$commit' before the deployment timeout."
}

if ($Profile -ne 'Primary') {
    $loadedDestinationHash = (Get-FileHash -LiteralPath $registeredDll -Algorithm SHA256).Hash
    if (-not [string]::Equals($loadedDestinationHash, $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
        Restore-DirectoryBackup -Backup $deploymentBackupRoot -Destination $deploymentTargetDirectory -OriginalFiles $deploymentOriginalFiles
        Remove-TemporaryBuildRoot $deploymentBackupRoot
        throw "$Profile CMB destination changed before loaded-runtime verification completed."
    }
    $deploymentReceipt['loadedDestinationSha256'] = $loadedDestinationHash
    $deploymentReceipt['backupDirectory'] = $null
    Remove-TemporaryBuildRoot $deploymentBackupRoot
}

Write-Receipt @{
    action = 'Deploy'
    profile = $Profile
    branch = $branch
    commit = $commit
    registeredDll = $registeredDll
    dllSha256 = $sourceHash
    assemblyVersion = $assemblyVersion
    productVersion = $productVersion
    before = $before
    after = $after
    bridgeDeployment = $deploymentReceipt
}
