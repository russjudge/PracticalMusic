<#
.SYNOPSIS
  Builds the Windows distribution of Practical Music.

.DESCRIPTION
  1. Publishes a self-contained win-x64 build (no .NET install needed on the target machine).
  2. Zips it to dist\PracticalMusic-<version>-win-x64.zip (portable: unzip and run PracticalMusic.exe).
  3. If Inno Setup 6 is installed (ISCC.exe on PATH or in Program Files), also builds
     dist\PracticalMusic-<version>-Setup.exe, an installer with Start-menu shortcut and .pmbook file association.

.PARAMETER Runtime
  win-x64 (default) or win-arm64.

.EXAMPLE
  pwsh packaging\windows\build-windows.ps1
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = $props.Project.PropertyGroup.Version
$dist = Join-Path $root "dist"
$publishDir = Join-Path $dist "$Runtime\PracticalMusic"

Write-Host "Practical Music $version - publishing $Runtime ($Configuration)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $publishDir | Out-Null

dotnet publish (Join-Path $root "src\PracticalMusic.Desktop\PracticalMusic.Desktop.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "README.md") $publishDir
Copy-Item (Join-Path $root "docs\INSTALL-Windows.md") $publishDir
Copy-Item (Join-Path $root "LICENSE") $publishDir -ErrorAction SilentlyContinue

$zip = Join-Path $dist "PracticalMusic-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable zip: $zip" -ForegroundColor Green

# Optional installer via Inno Setup
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { $iscc = Get-Command $found }
}
if ($iscc -and $Runtime -eq "win-x64") {
    & $iscc.Source "/DAppVersion=$version" "/DSourceDir=$publishDir" "/DOutputDir=$dist" (Join-Path $PSScriptRoot "PracticalMusic.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Installer: $dist\PracticalMusic-$version-Setup.exe" -ForegroundColor Green
} else {
    Write-Host "Inno Setup not found (or non-x64 runtime): installer skipped. Install from https://jrsoftware.org/isinfo.php to build the Setup.exe." -ForegroundColor Yellow
}
