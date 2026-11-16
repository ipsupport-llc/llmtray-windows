# The Microsoft Store package (adr/0025): the release's unsigned MSIX
# packages, x64 and ARM64, given the identity Partner Center assigned to the
# app, bundled and wrapped as one .msixupload. Nothing is rebuilt and
# nothing is signed: the Store signs what it accepts.
#
#   pwsh scripts/store-package.ps1 -Msix dist -Version 0.1.999.0 -Name <name> -Publisher 'CN=<guid>' -PublisherDisplayName <name> -Out store
param(
    [Parameter(Mandatory)] [string] $Msix,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $Publisher,
    [Parameter(Mandatory)] [string] $PublisherDisplayName,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'

# Partner Center's values, checked before anything is written: a typo would only show at certification.
if ($Name -notmatch '^[A-Za-z0-9.\-]{3,50}$') { throw "'$Name' is not a package name" }
if ($Publisher -notmatch '^CN=[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$') { throw "the Store publisher is CN=<GUID> from Partner Center, not '$Publisher'" }
if ($PublisherDisplayName.Trim().Length -eq 0) { throw 'the publisher display name is empty' }
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "'$Version' is not a Store version (the last number is 0)" }

# The newest Windows SDK's makeappx.
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem $kits -Directory | Where-Object Name -match '^10\.' | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe (Windows SDK) not found' }

$packages = @(Get-ChildItem $Msix -Filter *.msix)
if ($packages.Count -ne 2) { throw "expected the x64 and ARM64 .msix in $Msix, found $($packages.Count)" }

$work = Join-Path ([IO.Path]::GetTempPath()) ('llmtray-store-' + [guid]::NewGuid().ToString('N'))
$bundleIn = Join-Path $work 'bundle'
New-Item -ItemType Directory -Force $bundleIn, $Out | Out-Null
$seen = @()
try {
    foreach ($package in $packages) {
        $unpacked = Join-Path $work $package.BaseName
        & $makeappx unpack /p $package.FullName /d $unpacked /o /nv | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx couldn't unpack $($package.Name)" }
        # The footprint files makeappx writes anew (a signature too, if a signed package was given: the Store signs it anew).
        # -LiteralPath: [Content_Types] would otherwise be read as a wildcard.
        foreach ($f in 'AppxSignature.p7x', 'AppxBlockMap.xml', '[Content_Types].xml', 'AppxMetadata') {
            $p = Join-Path $unpacked $f
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
            if (Test-Path -LiteralPath $p) { throw "couldn't remove $f from the unpacked $($package.Name)" }
        }

        $manifestPath = Join-Path $unpacked 'AppxManifest.xml'
        [xml]$m = Get-Content -Raw $manifestPath
        if ($m.Package.Identity.Version -ne $Version) { throw "$($package.Name) is version $($m.Package.Identity.Version), not $Version" }
        $m.Package.Identity.Name = $Name
        $m.Package.Identity.Publisher = $Publisher
        $m.Package.Properties.PublisherDisplayName = $PublisherDisplayName
        $m.Save($manifestPath)

        $arch = $m.Package.Identity.ProcessorArchitecture
        if (-not $arch) { throw "$($package.Name) names no processor architecture" }
        if ($seen -contains $arch) { throw "two packages for $arch" }
        $seen += $arch
        & $makeappx pack /d $unpacked /p (Join-Path $bundleIn "LLMTray_$($Version)_$arch.msix") /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx couldn't pack the $arch package" }
        Write-Host "$arch package: $Name, $Version"
    }

    $bundle = Join-Path $work "LLMTray_$Version.msixbundle"
    & $makeappx bundle /d $bundleIn /p $bundle /bv $Version /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx couldn't bundle the packages" }

    # A .msixupload is a ZIP of the bundle (symbols would go beside it). ZipFile, not Compress-Archive: no 2 GB limit, any name.
    $upload = Join-Path (Resolve-Path $Out) "LLMTray_$Version.msixupload"
    if (Test-Path -LiteralPath $upload) { Remove-Item -LiteralPath $upload }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($upload, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $bundle, (Split-Path $bundle -Leaf), [IO.Compression.CompressionLevel]::NoCompression)
    }
    finally { $zip.Dispose() }
    Write-Host "$upload $([math]::Round((Get-Item $upload).Length / 1MB)) MB"
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $work
}
