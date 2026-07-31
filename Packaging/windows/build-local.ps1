# Builds the Windows MSI locally, mirroring what .github/workflows/release.yml does on
# windows-latest. Use it to check a change to Product.wxs without cutting a tag.
#
#     pwsh Packaging/windows/build-local.ps1
#     pwsh Packaging/windows/build-local.ps1 -Version 1.3.0 -Rid win-arm64
#
# Output lands in artifacts/, which is gitignored.
param(
    [string]$Version = "",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid = "win-x64",
    # The release always ships a dashboard; skip it here when iterating on the packaging itself
    # and Node is beside the point.
    [switch]$SkipDashboard
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$csproj = Join-Path $repoRoot "agentic-memory\agentic-memory.csproj"

# Default to whatever the csproj says, so a local MSI carries the same version a tag would stamp
# on it. release.yml enforces that the two agree; there is nothing to enforce here, only a
# sensible default to take.
if (-not $Version) {
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> found in $csproj" }
    Write-Host "Using <Version> from the csproj: $Version"
}

$publishDir = Join-Path $repoRoot "artifacts\publish\$Rid"
$outDir = Join-Path $repoRoot "artifacts\dist"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# A stale publish folder is worse than a slow one: `wix build` harvests whatever is in there, so a
# file removed from the project would still be picked up and shipped.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$publishArgs = @(
    "publish", $csproj, "-c", "Release", "-r", $Rid, "--self-contained", "true",
    "-p:Version=$Version", "-o", $publishDir
)
if ($SkipDashboard) { $publishArgs += "-p:SkipDashboardBuild=true" }

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not (Test-Path (Join-Path $publishDir "wwwroot\index.html"))) {
    Write-Warning "No wwwroot\index.html in the publish output. The MSI will install a server that 404s its dashboard."
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    dotnet tool install --global wix --version 7.0.0
}
wix eula accept wix7 | Out-Null
# -g caches the extension for the user rather than in a .wix folder under the working directory,
# which is otherwise created wherever this happens to be run from.
wix extension add -g WixToolset.UI.wixext | Out-Null

# arm64 packages declare arm64; everything else about the two is identical.
$arch = if ($Rid -eq "win-arm64") { "arm64" } else { "x64" }
$msi = Join-Path $outDir "agentic-memory-$Version-$Rid.msi"

wix build (Join-Path $repoRoot "Packaging\windows\Product.wxs") `
    -ext WixToolset.UI.wixext `
    -arch $arch `
    -d "ProductVersion=$Version.0" `
    -d "PublishDir=$publishDir" `
    -d "RepoRoot=$repoRoot" `
    -out $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# `wix build` does not validate in v7; validation is this separate command, so skipping it is the
# default rather than a choice. Running it catches the things that genuinely bite — duplicate
# component GUIDs, broken conditions, bad sequencing.
#
# The three suppressed ICEs are the standard false positives for a package that is per-user on
# purpose. ICE38 and ICE64 police per-machine packages that write into a user profile as a side
# effect, where a repair by a second user finds nothing; ICE91 warns that per-user directories do
# not vary with ALLUSERS. Here the entire product is registered per user, which is what makes all
# three moot. Satisfying ICE38 literally would mean inventing an HKCU key for every one of the
# several hundred harvested files.
wix msi validate -sice ICE38 -sice ICE64 -sice ICE91 $msi
if ($LASTEXITCODE -ne 0) { throw "wix msi validate failed" }

Write-Host "Built and validated $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
