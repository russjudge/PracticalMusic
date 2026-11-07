<#
.SYNOPSIS
  Builds the Android APK of Practical Music.

.DESCRIPTION
  Produces dist\android\PracticalMusic-<version>.apk, signed and ready to sideload (adb install or copy to the device).
  By default the APK is signed with the .NET debug keystore, which is fine for personal use and testing.
  For a release key set these environment variables before running:
      PM_KEYSTORE=C:\path\to\release.keystore   PM_KEYSTORE_PASS=...   PM_KEY_ALIAS=...   PM_KEY_PASS=...

  Requirements: .NET 10 SDK with the android workload (dotnet workload install android), Android SDK, JDK 17+.
  Pass -JavaHome / -AndroidSdk if they are not auto-detected.

.EXAMPLE
  pwsh packaging\android\build-android.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$AndroidSdk = $env:ANDROID_HOME
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = $props.Project.PropertyGroup.Version
$dist = Join-Path $root "dist\android"
New-Item -ItemType Directory -Force $dist | Out-Null

# Auto-detect common tool locations on Windows
if (-not $JavaHome) {
    $cands = @("$env:ProgramFiles\Android\openjdk", "$env:ProgramFiles\Microsoft", "$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Java")
    foreach ($c in $cands) {
        if (Test-Path $c) {
            $jdk = Get-ChildItem $c -Directory | Where-Object { $_.Name -match "jdk-?(1[7-9]|2[0-9])" } | Sort-Object Name -Descending | Select-Object -First 1
            if ($jdk) { $JavaHome = $jdk.FullName; break }
        }
    }
}
if (-not $AndroidSdk) {
    foreach ($c in @("${env:ProgramFiles(x86)}\Android\android-sdk", "$env:LOCALAPPDATA\Android\Sdk")) {
        if (Test-Path $c) { $AndroidSdk = $c; break }
    }
}
Write-Host "Practical Music $version - Android ($Configuration)" -ForegroundColor Cyan
Write-Host "  JDK:         $JavaHome"
Write-Host "  Android SDK: $AndroidSdk"

$args = @(
    "publish", (Join-Path $root "src\PracticalMusic.Android\PracticalMusic.Android.csproj"),
    "-c", $Configuration, "-f", "net10.0-android",
    "-p:AndroidPackageFormat=apk"
)
if ($JavaHome) { $args += "-p:JavaSdkDirectory=$JavaHome" }
if ($AndroidSdk) { $args += "-p:AndroidSdkDirectory=$AndroidSdk" }
if ($env:PM_KEYSTORE) {
    $args += @("-p:AndroidKeyStore=true", "-p:AndroidSigningKeyStore=$env:PM_KEYSTORE", "-p:AndroidSigningStorePass=$env:PM_KEYSTORE_PASS",
               "-p:AndroidSigningKeyAlias=$env:PM_KEY_ALIAS", "-p:AndroidSigningKeyPass=$env:PM_KEY_PASS")
}
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$apk = Get-ChildItem (Join-Path $root "src\PracticalMusic.Android\bin\$Configuration\net10.0-android") -Recurse -Filter "*-Signed.apk" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "No signed APK found" }
$out = Join-Path $dist "PracticalMusic-$version.apk"
Copy-Item $apk.FullName $out -Force
Write-Host "APK: $out" -ForegroundColor Green
Write-Host "Install on a connected device with: adb install -r `"$out`""
