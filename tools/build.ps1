<#
Builds the map program and packs it for a release:

    powershell -ExecutionPolicy Bypass -File tools\build.ps1

    dist\QueensDomainMap\QueensDomainMap.exe      the program, one file with everything in it (.NET runtime, page, exporter)
    dist\QueensDomainMap\README.txt, LICENSE.txt, THIRD-PARTY-NOTICES.txt
    dist\QueensDomainMap-<version>-win-x64.zip    the same four files, to attach to a GitHub release

Needs Windows and git. On first run it fetches two things into tools\_deps (about 1 GB, nothing is installed system-wide;
delete the folder to remove them again):
  - the .NET 10 SDK, with Microsoft's dotnet-install script (skipped when a .NET 10 SDK is installed already)
  - CUE4Parse (https://github.com/FabianFG/CUE4Parse), the open-source Unreal asset reader, at the commit this program
    was written against
NuGet packages are downloaded from nuget.org during the build.

-Dev makes the quick build for working on the program instead: tools\_deps\bin\QDExport.exe, a console program that
needs the .NET 10 runtime (run it with DOTNET_ROOT set to tools\_deps\dotnet when no runtime is installed).
#>
[CmdletBinding()]
param(
    [string]$Deps,      # default: tools\_deps
    [string]$Out,       # default: dist, beside tools
    [switch]$Dev
)
$ErrorActionPreference = 'Stop'
$CUE4ParseCommit = '4d2301a8eb129f593f1c76ff82612400801f1a94'
# (not as parameter defaults: Windows PowerShell 5.1 has no $PSScriptRoot yet at that point when started with -File)
$root = Split-Path $PSScriptRoot -Parent
if (-not $Deps) { $Deps = Join-Path $PSScriptRoot '_deps' }
if (-not $Out) { $Out = Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $Deps | Out-Null
$Deps = (Resolve-Path -LiteralPath $Deps).Path
$project = Join-Path $PSScriptRoot 'Exporter\QDExport.csproj'

# ---- the .NET 10 SDK
$dotnet = $null
$installed = Get-Command dotnet -ErrorAction SilentlyContinue
if ($installed -and ((& $installed.Source --list-sdks 2>$null) -match '^10\.')) { $dotnet = $installed.Source }
if (-not $dotnet) {
    $dotnet = Join-Path $Deps 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        Write-Host 'Downloading the .NET 10 SDK...'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $installer = Join-Path $Deps 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        if ((Get-AuthenticodeSignature $installer).Status -ne 'Valid') { throw 'dotnet-install.ps1 is not signed by Microsoft - aborting.' }
        & $installer -Channel 10.0 -InstallDir (Join-Path $Deps 'dotnet') -Architecture x64 -NoPath
    }
    $env:DOTNET_ROOT = Join-Path $Deps 'dotnet'
}

# ---- CUE4Parse
$cue = Join-Path $Deps 'CUE4Parse'
if (-not (Test-Path -LiteralPath (Join-Path $cue 'CUE4Parse\CUE4Parse.csproj'))) {
    Write-Host 'Fetching CUE4Parse...'
    New-Item -ItemType Directory -Force $cue | Out-Null
    git -C $cue init -q
    git -C $cue remote add origin https://github.com/FabianFG/CUE4Parse.git
    git -C $cue fetch -q --depth 1 origin $CUE4ParseCommit
    git -C $cue checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch CUE4Parse.' }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:CUE4PARSE_SKIP_NATIVE = 'true'      # its optional native helpers are not needed for this game
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $Deps 'nuget' }

# (switches are quoted at the calls: PowerShell would split -name:value in two on the way into a function)
function Invoke-DotNet {
    $lines = & $dotnet @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        $lines | Where-Object { "$_" -match '\berror\b' } | Select-Object -Unique -First 20 | ForEach-Object { Write-Host "$_" }
        throw "dotnet $($args[0]) failed."
    }
}

if ($Dev) {
    Write-Host 'Building the development program...'
    $bin = Join-Path $Deps 'bin'
    Invoke-DotNet build $project '-c' Release '-o' $bin '-v' q '-clp:NoSummary' "-p:CUE4ParseDir=$cue"
    Write-Host "Done: $(Join-Path $bin 'QDExport.exe')"
    return
}

# ---- the program
Write-Host 'Building QueensDomainMap.exe...'
$stage = Join-Path $Deps 'publish'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
# No debug files and no folder names of this PC inside the program: the compiler would otherwise write the full path
# of each project into it, user name included.
$env:PathMap = "$Deps=/_/deps,$root=/_/map"
Invoke-DotNet publish $project '-c' Release '-p:Dist=true' '-p:DebugType=none' '-p:DebugSymbols=false' '-o' $stage '-v' q '-clp:NoSummary' "-p:CUE4ParseDir=$cue"
$exe = Join-Path $stage 'QueensDomainMap.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'The build did not produce QueensDomainMap.exe.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

# ---- third-party notices: every package that went into the program, with the licence its author states
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Exporter\obj\project.assets.json') -Raw | ConvertFrom-Json
$packages = foreach ($lib in $assets.libraries.PSObject.Properties) {
    if ($lib.Value.type -ne 'package') { continue }
    $id, $ver = $lib.Name -split '/'
    if ($id -match '^(runtime\.|Microsoft\.NET\.ILLink|Microsoft\.DotNet\.ILCompiler|Microsoft\.NETCore\.App\.)') { continue }
    $nuspec = Join-Path $env:NUGET_PACKAGES "$($id.ToLower())\$ver\$($id.ToLower()).nuspec"
    $meta = if (Test-Path -LiteralPath $nuspec) { ([xml](Get-Content -LiteralPath $nuspec -Raw)).package.metadata } else { $null }
    $license = if ($meta.license.'#text') { $meta.license.'#text' } elseif ($meta.licenseUrl) { $meta.licenseUrl } else { 'see project page' }
    [pscustomobject]@{ Id = $id; Version = $ver; License = $license; Authors = $meta.authors; Copyright = $meta.copyright; Url = $meta.projectUrl }
}
$three = (Get-Content -LiteralPath (Join-Path $root 'lib\three.module.min.js') -TotalCount 6) -join ' '
$threeLine = if ($three -match 'Copyright[^*]*') { $Matches[0].Trim() } else { 'Copyright three.js authors' }
$notices = New-Object System.Text.StringBuilder
[void]$notices.AppendLine(@"
Queen's Domain Map $version - third-party notices
=================================================

This program contains the following software from other authors. Queen's Domain itself, and everything the program
reads from your copy of it, belongs to its developers and publisher; nothing of the game is part of this download.

.NET runtime - MIT licence - Copyright (c) .NET Foundation and Contributors - https://github.com/dotnet/runtime
three.js - MIT licence - $threeLine - https://threejs.org
CUE4Parse - Apache License 2.0 - https://github.com/FabianFG/CUE4Parse (commit $CUE4ParseCommit); its LICENSE and
  NOTICE files are reproduced at the end of this file.

NuGet packages (licence as stated by each package):

"@)
foreach ($p in $packages | Sort-Object Id) {
    [void]$notices.AppendLine("$($p.Id) $($p.Version) - $($p.License)")
    $who = @($p.Copyright, $p.Authors) | Where-Object { $_ } | Select-Object -First 1
    [void]$notices.AppendLine("  $who$(if ($p.Url) { " - $($p.Url)" })")
}
[void]$notices.AppendLine(@"

The MIT licence, under which most of the above are provided:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Packages under other licences (Apache-2.0, BSD and others) are provided under the terms at the address or licence
name given with them above.

----------------------------------------------------------------------------------------------------------------------
CUE4Parse: NOTICE
----------------------------------------------------------------------------------------------------------------------
"@)
foreach ($f in 'NOTICE', 'LICENSE') {
    $path = Join-Path $cue $f
    if ($f -eq 'LICENSE') {
        [void]$notices.AppendLine("`r`n" + ('-' * 118) + "`r`nCUE4Parse: LICENSE (Apache License 2.0)`r`n" + ('-' * 118))
    }
    if (Test-Path -LiteralPath $path) { [void]$notices.AppendLine((Get-Content -LiteralPath $path -Raw)) }
}

# ---- pack
$name = 'QueensDomainMap'
$pack = Join-Path $Out $name
if (Test-Path -LiteralPath $pack) { Remove-Item -LiteralPath $pack -Recurse -Force }
New-Item -ItemType Directory -Force $pack | Out-Null
Copy-Item -LiteralPath $exe -Destination $pack
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\README.txt') -Destination $pack
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $pack 'LICENSE.txt')
[IO.File]::WriteAllText((Join-Path $pack 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), (New-Object Text.UTF8Encoding $false))
$zip = Join-Path $Out "$name-$version-win-x64.zip"
Get-ChildItem -LiteralPath $Out -Filter "$name-*-win-x64.zip" -File | Remove-Item -Force      # this and earlier versions
Compress-Archive -Path $pack -DestinationPath $zip -CompressionLevel Optimal
# the build's intermediate files are several times the size of the result: gone again
foreach ($d in $stage, (Join-Path $PSScriptRoot 'Exporter\obj'), (Join-Path $PSScriptRoot 'Exporter\bin')) {
    if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
}
Write-Host ("Done: {0} ({1:N0} MB)" -f (Join-Path $pack "$name.exe"), ((Get-Item -LiteralPath (Join-Path $pack "$name.exe")).Length / 1MB))
Write-Host ("      {0} ({1:N0} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
