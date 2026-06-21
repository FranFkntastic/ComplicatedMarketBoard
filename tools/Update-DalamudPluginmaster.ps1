[CmdletBinding()]
param(
    [string] $SourceRepoJson = "",

    [string] $DalamudPluginsPath = "",

    [string] $PluginmasterPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($SourceRepoJson)) {
    $SourceRepoJson = Join-Path $repoRoot "dist\repo.json"
}

if ([string]::IsNullOrWhiteSpace($DalamudPluginsPath)) {
    $DalamudPluginsPath = Join-Path $repoRoot "..\DalamudPlugins"
}

if ([string]::IsNullOrWhiteSpace($PluginmasterPath)) {
    $PluginmasterPath = Join-Path $DalamudPluginsPath "pluginmaster.json"
}

$sourceRepoJsonFull = [System.IO.Path]::GetFullPath($SourceRepoJson)
$dalamudPluginsPathFull = [System.IO.Path]::GetFullPath($DalamudPluginsPath)
$pluginmasterPathFull = [System.IO.Path]::GetFullPath($PluginmasterPath)

if (-not (Test-Path $sourceRepoJsonFull)) {
    throw "Source repo json was not found: $sourceRepoJsonFull"
}

if (-not (Test-Path $dalamudPluginsPathFull)) {
    throw "DalamudPlugins repository was not found: $dalamudPluginsPathFull"
}

$sourceEntries = @(Get-Content -LiteralPath $sourceRepoJsonFull -Raw | ConvertFrom-Json)
if ($sourceEntries.Count -ne 1) {
    throw "Expected exactly one plugin entry in $sourceRepoJsonFull, found $($sourceEntries.Count)."
}

$sourceEntry = $sourceEntries[0]
if ([string]::IsNullOrWhiteSpace($sourceEntry.InternalName)) {
    throw "Source plugin entry is missing InternalName."
}

if ([string]::IsNullOrWhiteSpace($sourceEntry.AssemblyVersion)) {
    throw "Source plugin entry is missing AssemblyVersion."
}

if ([string]::IsNullOrWhiteSpace($sourceEntry.RepoUrl)) {
    throw "Source plugin entry is missing RepoUrl."
}

$expectedPackageUrl = "$($sourceEntry.RepoUrl.TrimEnd('/'))/releases/download/v$($sourceEntry.AssemblyVersion)/latest.zip"
foreach ($propertyName in @("DownloadLinkInstall", "DownloadLinkTesting", "DownloadLinkUpdate")) {
    if ($sourceEntry.$propertyName -ne $expectedPackageUrl) {
        throw "$propertyName must use the versioned release URL: $expectedPackageUrl"
    }
}

$response = Invoke-WebRequest -Uri $expectedPackageUrl -Method Head -UseBasicParsing
if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 400) {
    throw "Package URL returned HTTP $($response.StatusCode): $expectedPackageUrl"
}

$pluginmasterEntries = @()
if (Test-Path $pluginmasterPathFull) {
    $pluginmasterEntries = @(Get-Content -LiteralPath $pluginmasterPathFull -Raw | ConvertFrom-Json)
}

$updatedEntries = @($pluginmasterEntries | Where-Object { $_.InternalName -ne $sourceEntry.InternalName })
$updatedEntries += $sourceEntry
$duplicateNames = @($updatedEntries | Group-Object -Property InternalName | Where-Object { $_.Count -gt 1 })
if ($duplicateNames.Count -gt 0) {
    throw "Pluginmaster contains duplicate InternalName values: $($duplicateNames.Name -join ', ')"
}

$pluginmasterJson = ConvertTo-Json -InputObject @($updatedEntries) -Depth 8
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($pluginmasterPathFull, $pluginmasterJson, $utf8NoBom)

$validatedEntries = @(Get-Content -LiteralPath $pluginmasterPathFull -Raw | ConvertFrom-Json)
$updatedEntry = $validatedEntries | Where-Object { $_.InternalName -eq $sourceEntry.InternalName } | Select-Object -First 1
if ($null -eq $updatedEntry) {
    throw "Updated pluginmaster does not contain $($sourceEntry.InternalName)."
}

Write-Host "Updated Dalamud pluginmaster:"
Write-Host "  Source: $sourceRepoJsonFull"
Write-Host "  Target: $pluginmasterPathFull"
Write-Host "  Plugin: $($sourceEntry.InternalName) $($sourceEntry.AssemblyVersion)"
Write-Host "  Package URL: $expectedPackageUrl"
Write-Host "  Package check: HTTP $($response.StatusCode)"
