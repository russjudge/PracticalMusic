<#
.SYNOPSIS
  Builds the Practical Music website for a Linux server (from Windows).

.DESCRIPTION
  Publishes a self-contained build (the server needs no .NET installation) and packs it, together with the SQL
  scripts, the systemd unit, the Apache site and the documentation, into dist\practicalmusic-web-<version>-<rid>.tar.gz.
  Copy that file to the server and follow docs\INSTALL-Server-Debian.md.

.PARAMETER Runtime
  linux-x64 (default) or linux-arm64.

.EXAMPLE
  pwsh packaging\web\build-web.ps1
#>
param(
    [string]$Runtime = "linux-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = $props.Project.PropertyGroup.Version
$dist = Join-Path $root "dist"
$stageRoot = Join-Path $dist "web-$Runtime"
$stage = Join-Path $stageRoot "practicalmusic-web"

Write-Host "Practical Music website $version - publishing $Runtime ($Configuration)" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "app"), (Join-Path $stage "database"), (Join-Path $stage "server") | Out-Null

dotnet publish (Join-Path $root "src\PracticalMusic.Web\PracticalMusic.Web.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none `
    -o (Join-Path $stage "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Remove-Item (Join-Path $stage "app\appsettings.Development.json") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "database\*.sql") (Join-Path $stage "database")
foreach ($f in "practicalmusic-web.service", "practicalmusic.russjudge.com.conf", "appsettings.Production.example.json") {
    Copy-Item (Join-Path $root "packaging\web\$f") (Join-Path $stage "server")
}
foreach ($f in "INSTALL-Server-Debian.md", "EMAIL-SETUP.md", "WEBSITE.md") { Copy-Item (Join-Path $root "docs\$f") $stage }

# tar keeps the program's execute permission only when it is set at extraction; the install steps run chmod as well.
$tarball = Join-Path $dist "practicalmusic-web-$version-$Runtime.tar.gz"
if (Test-Path $tarball) { Remove-Item $tarball }
tar -C $stageRoot -czf $tarball practicalmusic-web
if ($LASTEXITCODE -ne 0) { throw "tar failed" }
Write-Host "Done: $tarball" -ForegroundColor Green
