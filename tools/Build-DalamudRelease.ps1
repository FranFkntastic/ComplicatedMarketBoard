[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $PackageUrl = "",

    [string] $RepositoryUrl = "https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json",

    [string] $OutputDirectory = "",

    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solutionPath = Join-Path $repoRoot "ComplicatedMarketBoard.sln"
$projectDir = Join-Path $repoRoot "ComplicatedMarketBoard"
$pluginName = "ComplicatedMarketBoard"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "dist"
}

$outputDirectoryFull = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot)
if (-not $outputDirectoryFull.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside the repository: $repoRootFull"
}

$buildOutput = Join-Path $projectDir "bin\$Configuration"
$packageStaging = Join-Path $OutputDirectory "package"
$zipPath = Join-Path $OutputDirectory "latest.zip"
$repoJsonPath = Join-Path $OutputDirectory "repo.json"

if (-not $SkipBuild) {
    if (Test-Path $buildOutput) {
        Remove-Item -LiteralPath $buildOutput -Recurse -Force
    }

    dotnet build $solutionPath -c $Configuration -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$manifestPath = Join-Path $buildOutput "$pluginName.json"
if (-not (Test-Path $manifestPath)) {
    throw "Expected manifest was not found: $manifestPath"
}

if (Test-Path $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $packageStaging | Out-Null

$packageFiles = @(
    "$pluginName.dll",
    "$pluginName.deps.json",
    "$pluginName.json",
    "$pluginName.xml",
    "Miosuke.dll",
    "Miosuke.xml"
)

foreach ($fileName in $packageFiles) {
    $sourcePath = Join-Path $buildOutput $fileName
    if (-not (Test-Path $sourcePath)) {
        throw "Expected package file was not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination $packageStaging
}

$noticeFiles = @(
    "LICENSE",
    "NOTICE.md"
)

foreach ($fileName in $noticeFiles) {
    $sourcePath = Join-Path $repoRoot $fileName
    if (-not (Test-Path $sourcePath)) {
        throw "Expected notice file was not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination $packageStaging
}

Compress-Archive -Path (Join-Path $packageStaging "*") -DestinationPath $zipPath -Force

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseTag = "v$($manifest.AssemblyVersion)"
if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
    $PackageUrl = "https://github.com/FranFkntastic/ComplicatedMarketBoard/releases/download/$releaseTag/latest.zip"
}

$lastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$rawBaseUrl = "https://raw.githubusercontent.com/FranFkntastic/ComplicatedMarketBoard/master"

$repoEntry = [ordered]@{
    Author = $manifest.Author
    Name = $manifest.Name
    InternalName = $manifest.InternalName
    AssemblyVersion = $manifest.AssemblyVersion
    TestingAssemblyVersion = $null
    Description = $manifest.Description
    ApplicableVersion = $manifest.ApplicableVersion
    RepoUrl = $manifest.RepoUrl
    DalamudApiLevel = $manifest.DalamudApiLevel
    Punchline = $manifest.Punchline
    Tags = $manifest.Tags
    CategoryTags = $manifest.CategoryTags
    IsHide = $false
    IsTestingExclusive = $false
    DownloadCount = 0
    DownloadLinkInstall = $PackageUrl
    DownloadLinkTesting = $PackageUrl
    DownloadLinkUpdate = $PackageUrl
    LastUpdate = $lastUpdate
    IconUrl = "$rawBaseUrl/images/icon.png"
    ImageUrls = @(
        "$rawBaseUrl/images/image1.png",
        "$rawBaseUrl/images/image2.png",
        "$rawBaseUrl/images/image3.png"
    )
}

$repoEntryJson = $repoEntry | ConvertTo-Json -Depth 8
$repoJson = "[$repoEntryJson]"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($repoJsonPath, $repoJson, $utf8NoBom)

Write-Host "Built Dalamud package:"
Write-Host "  Zip:  $zipPath"
Write-Host "  Repo: $repoJsonPath"
Write-Host "  Package URL: $PackageUrl"
Write-Host ""
Write-Host "Upload latest.zip to the GitHub Release tagged $releaseTag, then add this custom repository URL in Dalamud:"
Write-Host "  $RepositoryUrl"
