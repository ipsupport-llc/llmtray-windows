# Writes src/LLMTray.App/runtime/Licenses.json and THIRD_PARTY_NOTICES.txt
# (shipped beside the app, listed in its About tab) for one build. Run
# after scripts/fetch-llama.ps1 and before building the app:
#   pwsh scripts/generate-licenses.ps1 -Rid win-x64 -Platform x64
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][ValidateSet('x64', 'ARM64')][string]$Platform
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'src/LLMTray.App'
dotnet restore $app -r $Rid -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
$assets = Join-Path $app 'obj/project.assets.json'
$target = (Get-Content $assets -Raw | ConvertFrom-Json).targets.PSObject.Properties.Name | Where-Object { $_ -like "*/$Rid" } | Select-Object -First 1
if (-not $target) { throw "no $Rid target in $assets" }
$packages = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
$dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
dotnet run --project (Join-Path $root 'tools/LLMTray.Licenses') -- $root $assets $target $packages $dotnetRoot (Join-Path $app 'runtime')
if ($LASTEXITCODE -ne 0) { throw 'license generation failed' }
