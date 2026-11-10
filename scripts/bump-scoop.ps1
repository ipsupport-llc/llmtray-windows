# Points the Scoop manifest (ipsupport-llc/scoop-bucket, adr/0005) at a
# release's portable ZIPs, with the SHA-256 of the ZIPs themselves. Run by
# release.yml after publishing; nothing to do by hand.
#   pwsh scripts/bump-scoop.ps1 -Version 0.1.0-beta.5 -Dist dist -Manifest ../scoop-bucket/bucket/llmtray.json
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Dist,
    [Parameter(Mandatory)][string]$Manifest
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-beta\.\d+)?$') { throw "'$Version' is not a release version" }

# Release order, as the app's ReleaseTag sorts it: the betas of X.Y.Z come
# before X.Y.Z itself.
function Get-Order([string]$v) {
    $m = [regex]::Match($v, '^(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?$')
    if (-not $m.Success) { return [version]'0.0.0.0' }
    $beta = if ($m.Groups[4].Success) { [int]$m.Groups[4].Value } else { 999 }
    [version]::new([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value * 1000 + $beta)
}

$path = $Manifest
$data = Get-Content $path -Raw | ConvertFrom-Json
if ((Get-Order $data.version) -gt (Get-Order $Version)) {
    Write-Host "the bucket already has $($data.version), newer than ${Version}: left as it is"
    exit 0
}
# Every ZIP first: a missing one leaves the manifest untouched.
$hashes = @{}
foreach ($name in 'x64', 'ARM64') {
    $zip = Join-Path $Dist "LLMTray-$Version-$name-portable.zip"
    if (-not (Test-Path $zip)) { throw "$(Split-Path $zip -Leaf) is not in $Dist" }
    $hashes[$name] = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
}
$data.version = $Version
foreach ($pair in @(@('64bit', 'x64'), @('arm64', 'ARM64'))) {
    $file = "LLMTray-$Version-$($pair[1])-portable.zip"
    $data.architecture.($pair[0]).url = "https://github.com/ipsupport-llc/llmtray-windows/releases/download/v$Version/$file"
    $data.architecture.($pair[0]).hash = $hashes[$pair[1]]
}
# Scoop's own style: four-space indents.
$json = $data | ConvertTo-Json -Depth 10
$json = ($json -split "`n" | ForEach-Object { $_ -replace '^( +)', { ' ' * ($_.Groups[1].Value.Length * 2) } }) -join "`n"
Set-Content $path ($json.TrimEnd() + "`n") -NoNewline
Write-Host "$path -> $Version"
