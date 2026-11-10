# Builds the portable ZIP (adr/0004): the app unpackaged, self-contained,
# with llama.cpp, in one LLMTray folder. Fetch llama.cpp first
# (scripts/fetch-llama.ps1).
#   pwsh scripts/build-portable.ps1 -Rid win-x64 -Platform x64 -Release 0.1.0-beta.2 -Zip out/LLMTray-0.1.0-beta.2-x64-portable.zip
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][ValidateSet('x64', 'ARM64')][string]$Platform,
    [Parameter(Mandatory)][string]$Release,
    [Parameter(Mandatory)][string]$Zip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path ([IO.Path]::GetTempPath()) "llmtray-portable-$Rid"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$folder = Join-Path $stage 'LLMTray'

# Run it in a clean checkout (CI does): a packaged build's outputs in obj/
# would mix in.
dotnet publish (Join-Path $root 'src/LLMTray.App') -c Release -r $Rid -p:Platform=$Platform `
    -p:WindowsPackageType=None -p:GenerateAppxPackageOnBuild=false `
    -p:LLMTrayRelease=$Release `
    -o $folder
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
foreach ($required in 'LLMTray.exe', 'runtime\llama\cpu\llama-server.exe', 'Assets\AppIcon.ico', 'NOTICE.md', 'Strings\de.json') {
    if (-not (Test-Path (Join-Path $folder $required))) { throw "the portable build has no $required" }
}
New-Item -ItemType Directory -Force (Split-Path $Zip -Parent) | Out-Null
if (Test-Path $Zip) { Remove-Item $Zip }
Compress-Archive -Path $folder -DestinationPath $Zip -CompressionLevel Optimal
Write-Host "$Zip $([math]::Round((Get-Item $Zip).Length / 1MB)) MB"
