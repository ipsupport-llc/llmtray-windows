# Puts the pinned llama.cpp backends (runtime-pins.json) into
# src/LLMTray.App/runtime/llama/<backend>/ for one architecture, checking each
# zip's SHA-256. Run before building the app:
#   pwsh scripts/fetch-llama.ps1 -Rid win-x64
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pins = (Get-Content (Join-Path $root 'runtime-pins.json') -Raw | ConvertFrom-Json).'llama.cpp'
$target = Join-Path $root 'src/LLMTray.App/runtime/llama'
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
$cache = Join-Path ([IO.Path]::GetTempPath()) 'llmtray-llama'
New-Item -ItemType Directory -Force $cache | Out-Null

foreach ($backend in $pins.assets.$Rid.PSObject.Properties) {
    $asset = $backend.Value
    $zip = Join-Path $cache $asset.file
    if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $asset.sha256) {
        $url = $pins.url.Replace('{build}', $pins.build).Replace('{file}', $asset.file)
        Write-Host "Downloading $url"
        Invoke-WebRequest $url -OutFile $zip
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $asset.sha256) { throw "$($asset.file): SHA-256 $hash, expected $($asset.sha256)" }
    $dir = Join-Path $target $backend.Name
    $unpacked = Join-Path $cache "unpacked-$($backend.Name)"
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    Expand-Archive $zip -DestinationPath $unpacked
    # The server and its DLLs sit together, at the top or one folder down.
    $server = Get-ChildItem $unpacked -Recurse -Filter llama-server.exe | Select-Object -First 1
    if (-not $server) { throw "$($asset.file) has no llama-server.exe" }
    Move-Item $server.DirectoryName $dir
    Write-Host "$($backend.Name): $dir"
}

# The license texts of llama.cpp and what it vendors, for About and
# THIRD_PARTY_NOTICES (tools/LLMTray.Licenses), pinned like the zips.
$licenses = Join-Path $target 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
foreach ($file in $pins.licenses.files) {
    $url = $pins.licenses.source.Replace('{build}', $pins.build).Replace('{path}', $file.path)
    $dest = Join-Path $licenses $file.as
    Invoke-WebRequest $url -OutFile $dest
    $hash = (Get-FileHash $dest -Algorithm SHA256).Hash
    if ($hash -ne $file.sha256) { throw "$($file.path): SHA-256 $hash, expected $($file.sha256)" }
}
Write-Host "licenses: $licenses"
