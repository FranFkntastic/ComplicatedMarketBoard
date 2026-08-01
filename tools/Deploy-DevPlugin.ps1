[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'Claim', 'Deploy', 'Release')]
    [string]$Action,

    [ValidateSet('Primary', 'Secondary')]
    [string]$Profile = 'Primary',

    [string]$Owner,

    [string]$ExpectedCommit,

    [string]$DabPath,

    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repository
$project = Join-Path $repository 'ComplicatedMarketBoard\ComplicatedMarketBoard.csproj'
$sourceDirectory = Join-Path $repository 'ComplicatedMarketBoard\bin\Debug'
$sourceDll = Join-Path $sourceDirectory 'ComplicatedMarketBoard.dll'
$profileKey = $Profile.ToLowerInvariant()
$profileDirectoryName = if ($Profile -eq 'Primary') { 'XIVLauncher' } else { 'XIVLauncher-Multibox-2' }
$dabProfile = if ($Profile -eq 'Primary') { 'primary' } else { 'XIVLauncher-Multibox-2' }
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

function Copy-DirectoryFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$MainDllName
    )

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    $files = [System.IO.Directory]::EnumerateFiles($Source, '*', [System.IO.SearchOption]::AllDirectories) |
        Sort-Object { if ([System.IO.Path]::GetRelativePath($Source, $_) -eq $MainDllName) { 1 } else { 0 } }
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($Source, $file)
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
        [Parameter(Mandatory = $true)][string[]]$OriginalFiles
    )

    if (-not (Test-Path -LiteralPath $Backup -PathType Container)) {
        return
    }
    $originalSet = [System.Collections.Generic.HashSet[string]]::new($OriginalFiles, [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in [System.IO.Directory]::EnumerateFiles($Destination, '*', [System.IO.SearchOption]::AllDirectories)) {
        $relative = [System.IO.Path]::GetRelativePath($Destination, $file)
        if (-not $originalSet.Contains($relative)) {
            [System.IO.File]::Delete($file)
        }
    }
    Copy-DirectoryFiles -Source $Backup -Destination $Destination -MainDllName 'ComplicatedMarketBoard.dll'
}

if ($Action -eq 'Status') {
    $entry = Get-CmbCatalogEntry (Resolve-DabPath)
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
$originCommit = Get-RepositoryValue @('rev-parse', 'origin/local-dev')
$dirty = Get-RepositoryValue @('status', '--porcelain=v1', '--untracked-files=all')
if ($branch -ne 'local-dev') {
    throw "CMB deployment requires branch 'local-dev'; current branch is '$branch'."
}
if (-not [string]::IsNullOrWhiteSpace($dirty)) {
    throw "CMB deployment requires a clean worktree."
}
if ($commit -ne $originCommit) {
    throw "CMB local-dev '$commit' does not match origin/local-dev '$originCommit'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and -not $commit.StartsWith($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "CMB local-dev '$commit' does not match expected commit '$ExpectedCommit'."
}

$franthropy = Join-Path $workspace 'Franthropy'
$franthropyDirty = (& git -C $franthropy status --porcelain=v1 --untracked-files=all | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace($franthropyDirty)) {
    throw "Franthropy must be a clean sibling checkout before CMB deployment."
}
& git -C $franthropy merge-base --is-ancestor HEAD origin/main
if ($LASTEXITCODE -ne 0) {
    throw "Franthropy HEAD is not published on origin/main."
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
if ($Profile -eq 'Secondary') {
    $temporaryBuildRoot = Join-Path ([System.IO.Path]::GetTempPath()) "cmb-secondary-$([Guid]::NewGuid().ToString('N'))"
    $buildDirectory = Join-Path $temporaryBuildRoot 'output'
}

$buildArguments = @('build', $project, '-c', 'Debug', '--no-restore', '--no-incremental')
if ($Profile -eq 'Secondary') {
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
if ($Profile -eq 'Secondary') {
    try {
        $deploymentTargetDirectory = [System.IO.Path]::GetDirectoryName($registeredDll)
        $deploymentBackupRoot = Join-Path ([System.IO.Path]::GetTempPath()) "cmb-secondary-backup-$([Guid]::NewGuid().ToString('N'))"
        [System.IO.Directory]::CreateDirectory($deploymentBackupRoot) | Out-Null
        if (Test-Path -LiteralPath $deploymentTargetDirectory -PathType Container) {
            $deploymentOriginalFiles = @([System.IO.Directory]::EnumerateFiles(
                $deploymentTargetDirectory,
                '*',
                [System.IO.SearchOption]::AllDirectories) | ForEach-Object {
                    [System.IO.Path]::GetRelativePath($deploymentTargetDirectory, $_)
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
    if ($Profile -eq 'Secondary') {
        Restore-DirectoryBackup -Backup $deploymentBackupRoot -Destination $deploymentTargetDirectory -OriginalFiles $deploymentOriginalFiles
        Remove-TemporaryBuildRoot $deploymentBackupRoot
    }
    throw "$Profile did not advertise the expected CMB commit '$commit' before the deployment timeout."
}

if ($Profile -eq 'Secondary') {
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
