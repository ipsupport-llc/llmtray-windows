# Points bucket/llmtray.json (adr/0005) at a release's portable ZIPs, with
# the SHA-256 of the ZIPs themselves. Run by release.yml after publishing;
# nothing to do by hand.
#   pwsh scripts/bump-scoop.ps1 -Version 0.1.0-beta.5 -Dist dist
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Dist
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$path = Join-Path $root 'bucket/llmtray.json'
$manifest = Get-Content $path -Raw | ConvertFrom-Json
$manifest.version = $Version
foreach ($pair in @(@('64bit', 'x64'), @('arm64', 'ARM64'))) {
    $file = "LLMTray-$Version-$($pair[1])-portable.zip"
    $zip = Join-Path $Dist $file
    if (-not (Test-Path $zip)) { throw "$file is not in $Dist" }
    $manifest.architecture.($pair[0]).url = "https://github.com/ipsupport-llc/llmtray-windows/releases/download/v$Version/$file"
    $manifest.architecture.($pair[0]).hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
}
# Scoop's own style: four-space indents.
$json = $manifest | ConvertTo-Json -Depth 10
$json = ($json -split "`n" | ForEach-Object { $_ -replace '^( +)', { ' ' * ($_.Groups[1].Value.Length * 2) } }) -join "`n"
Set-Content $path ($json.TrimEnd() + "`n") -NoNewline
Write-Host "bucket/llmtray.json -> $Version"
