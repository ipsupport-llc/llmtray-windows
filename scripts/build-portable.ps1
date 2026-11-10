# Builds the portable ZIP and the per-user installer (adr/0004, adr/0007):
# the app unpackaged and self-contained with llama.cpp, laid out as
#   LLMTray\LLMTray.exe   the small launcher
#   LLMTray\README.txt
#   LLMTray\app\...       the app, its runtimes, llama.cpp, translations
# Run in a clean checkout after scripts/fetch-llama.ps1 (and the license and
# translation steps CI runs).
#   pwsh scripts/build-portable.ps1 -Rid win-x64 -Platform x64 -Release 0.1.0-beta.5 -Zip out/LLMTray-0.1.0-beta.5-x64-portable.zip [-Setup out]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][ValidateSet('x64', 'ARM64')][string]$Platform,
    [Parameter(Mandatory)][string]$Release,
    [Parameter(Mandatory)][string]$Zip,
    # A folder: also build LLMTray-<release>-<platform>-setup.exe there.
    [string]$Setup
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path ([IO.Path]::GetTempPath()) "llmtray-portable-$Rid"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$folder = Join-Path $stage 'LLMTray'

dotnet publish (Join-Path $root 'src/LLMTray.App') -c Release -r $Rid -p:Platform=$Platform `
    -p:WindowsPackageType=None -p:GenerateAppxPackageOnBuild=false `
    -p:LLMTrayRelease=$Release `
    -o (Join-Path $folder 'app')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
dotnet publish (Join-Path $root 'src/LLMTray.Launcher') -c Release -r $Rid -o (Join-Path $stage 'launcher')
if ($LASTEXITCODE -ne 0) { throw 'launcher publish failed' }
Copy-Item (Join-Path $stage 'launcher/LLMTray.exe') (Join-Path $folder 'LLMTray.exe')
@"
LLMTray $Release for Windows ($Platform)

Start LLMTray.exe. It lives in the notification area; its window has the
models, the chat and the settings. The app\ folder holds everything it runs
on: leave it beside LLMTray.exe.

Settings and logs: %LOCALAPPDATA%\LLMTray
Models:            %USERPROFILE%\.llmtray\models

https://github.com/ipsupport-llc/llmtray-windows
"@ | Set-Content (Join-Path $folder 'README.txt') -Encoding utf8

# What the build put in app\: the installer and the updater remove the rest
# (adr/0007). Relative paths, one per line.
$appDir = Join-Path $folder 'app'
$list = @(Get-ChildItem $appDir -Recurse -File | ForEach-Object { $_.FullName.Substring($appDir.Length + 1) }) + 'files.txt'
[IO.File]::WriteAllLines((Join-Path $appDir 'files.txt'), [string[]]($list | Sort-Object -Unique), [Text.UTF8Encoding]::new($false))

foreach ($required in 'LLMTray.exe', 'app\LLMTray.exe', 'app\cli\llmtray.exe', 'app\runtime\llama\cpu\llama-server.exe', 'app\Assets\AppIcon.ico', 'app\NOTICE.md', 'app\Strings\de.json') {
    if (-not (Test-Path (Join-Path $folder $required))) { throw "the build has no $required" }
}
New-Item -ItemType Directory -Force (Split-Path $Zip -Parent) | Out-Null
if (Test-Path $Zip) { Remove-Item $Zip }
Compress-Archive -Path $folder -DestinationPath $Zip -CompressionLevel Optimal
Write-Host "$Zip $([math]::Round((Get-Item $Zip).Length / 1MB)) MB"

if ($Setup) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        choco install innosetup --no-progress -y | Out-Null
        $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    }
    New-Item -ItemType Directory -Force $Setup | Out-Null
    # The file version, as for the MSIX (adr/0002): X.Y.(Z*1000+N).0, 999 for a stable release.
    if ($Release -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?$') { throw "not a release version: $Release" }
    $n = if ($Matches[4]) { [int]$Matches[4] } else { 999 }
    $numeric = "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] * 1000 + $n).0"
    $out = (Resolve-Path $Setup).Path
    $exe = Join-Path $out "LLMTray-$Release-$Platform-setup.exe"
    if (Test-Path $exe) { Remove-Item $exe }
    & $iscc /Q "/DAppVersion=$Release" "/DNumVersion=$numeric" "/DArch=$Platform" "/DSource=$folder" "/DOutput=$out" (Join-Path $root 'installer/llmtray.iss')
    if ($LASTEXITCODE -ne 0) { throw 'the installer did not build' }
    if (-not (Test-Path $exe)) { throw "the installer build made no $exe" }
    Write-Host "$exe $([math]::Round((Get-Item $exe).Length / 1MB)) MB"
}
