[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'Claim', 'Deploy', 'Release')]
    [string]$Action,

    [ValidateSet('Primary')]
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
$profileRoot = Join-Path $env:APPDATA 'XIVLauncher'
$configPath = Join-Path $profileRoot 'dalamudConfig.json'
$laneRoot = Join-Path $env:LOCALAPPDATA 'FranFkntastic\ComplicatedMarketBoard\dev-lanes'
$leasePath = Join-Path $laneRoot 'primary.json'

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
        throw "CMB Primary lease is unreadable at '$leasePath': $($_.Exception.Message)"
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

    $json = & $Executable plugins --profile primary | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "DAB could not read the Primary plugin catalog."
    }

    $catalog = $json | ConvertFrom-Json
    $matches = @($catalog.plugins | Where-Object {
        $_.internalName -eq 'ComplicatedMarketBoard' -and $_.isLoaded -and $_.isDev
    })
    if ($matches.Count -ne 1) {
        throw "Primary must expose exactly one loaded ComplicatedMarketBoard development entry; found $($matches.Count)."
    }

    return $matches[0]
}

function Get-RegisteredDllPath {
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Primary Dalamud configuration was not found at '$configPath'."
    }

    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $locations = @($config.DevPluginLoadLocations.'$values' | Where-Object {
        $_.IsEnabled -and [System.IO.Path]::GetFileName($_.Path) -eq 'ComplicatedMarketBoard.dll'
    })
    if ($locations.Count -ne 1) {
        throw "Primary must have exactly one enabled ComplicatedMarketBoard development registration; found $($locations.Count)."
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
        throw "CMB Primary is claimed by '$($activeLease.owner)' until $($activeLease.expiresAtUtc)."
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
    throw "CMB Primary is not claimed by '$Owner'. Run -Action Claim first."
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
if (-not [string]::Equals(
    [System.IO.Path]::GetFullPath($sourceDll),
    $registeredDll,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Primary watches '$registeredDll', not this integration worktree's '$sourceDll'."
}

$dab = Resolve-DabPath
$before = Get-CmbCatalogEntry $dab
if (-not $before.isLoaded -or -not $before.isDev) {
    throw "Primary ComplicatedMarketBoard must be a loaded development plugin before deployment."
}

& dotnet build $project -c Debug --no-restore --no-incremental
if ($LASTEXITCODE -ne 0) {
    throw "CMB Debug build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $sourceDll -PathType Leaf)) {
    throw "CMB build did not produce '$sourceDll'."
}

$sourceHash = (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($sourceDll).Version.ToString()
$productVersion = (Get-Item -LiteralPath $sourceDll).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or $productVersion -notlike "*$commit*") {
    throw "CMB artifact product version '$productVersion' does not carry integration commit '$commit'."
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
    throw "Primary did not advertise the expected CMB commit '$commit' before the deployment timeout."
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
}
