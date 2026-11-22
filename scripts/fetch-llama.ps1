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

# The Visual C++ runtime llama.cpp's Windows builds link against
# (VCRUNTIME140, VCRUNTIME140_1, MSVCP140) isn't part of Windows: on a PC
# without the redistributable, Windows refused to start llama-server (exit
# code 0xC0000135, a DLL not found) before it wrote a line, while CI, which
# has it installed, passed. Microsoft lets an app carry these DLLs beside its
# programs (app-local deployment); they come from this machine's Visual Studio
# redistributable folder, for the architecture being built.
$arch = @{ 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }[$Rid]
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found: Visual Studio is needed for its Visual C++ runtime" }
$vs = & $vswhere -latest -products * -property installationPath
$crt = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Get-ChildItem (Join-Path $_.FullName $arch) -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue } |
    Select-Object -First 1
if (-not $crt) { throw "no Visual C++ runtime for $arch under $vs" }
Write-Host "Visual C++ runtime: $($crt.FullName)"
foreach ($dir in Get-ChildItem $target -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'llama-server.exe') }) {
    foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
        $file = Join-Path $crt.FullName $dll
        if (Test-Path $file) { Copy-Item $file $dir.FullName }
    }
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
# LLVM's OpenMP runtime (libomp.dll) ships in the zips with its license beside it.
$omp = Get-ChildItem (Join-Path $target 'cpu') -Filter 'LICENSE-LLVM-OpenMP' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $omp) { throw "LICENSE-LLVM-OpenMP is not in the CPU build" }
Copy-Item $omp.FullName (Join-Path $licenses 'LICENSE-LLVM-OpenMP')
Write-Host "licenses: $licenses"

# Every DLL each build needs is beside it or part of Windows: nothing else to install (tools/LLMTray.Deps).
dotnet run --project (Join-Path $root 'tools/LLMTray.Deps') -- $target
if ($LASTEXITCODE -ne 0) { throw 'a llama.cpp build needs a DLL that neither it nor Windows has' }
