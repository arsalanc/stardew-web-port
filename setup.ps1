<#
.SYNOPSIS
    Prepares the browser port from YOUR copy of Stardew Valley.

.DESCRIPTION
    Nothing from the game is stored in this repository. This script:
      1. finds your Stardew Valley install (Steam or GOG), or uses -GameDir
      2. decompiles the game and three of its libraries into local/ (git-ignored)
      3. applies the browser-port patches (tools/PortPatcher)
      4. exports the game's audio for Web Audio (tools/AudioExport)
      5. builds the dev server
    Re-run it any time (e.g. after the game updates); it starts from a fresh decompile.

.PARAMETER GameDir
    Folder containing "Stardew Valley.dll" and Content\. Auto-detected for Steam and GOG installs.

.EXAMPLE
    .\setup.ps1
    .\setup.ps1 -GameDir "D:\Games\Stardew Valley"
#>
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$local = Join-Path $repo 'local'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run([string]$what, [scriptblock]$command) {
    # Judge native commands by exit code: Windows PowerShell treats any stderr output as an error under 'Stop'.
    $ErrorActionPreference = 'Continue'
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

function Find-GameDir {
    $candidates = @()
    # Steam: the install path plus every library folder.
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steam) {
        $libraries = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $libraries += Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
        }
        $candidates += $libraries | ForEach-Object { Join-Path $_ 'steamapps\common\Stardew Valley' }
    }
    # GOG.
    $gog = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games\1453375253' -ErrorAction SilentlyContinue).path
    if ($gog) { $candidates += $gog }
    $candidates += 'C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley', 'C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley'
    $candidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'Stardew Valley.dll')) } | Select-Object -First 1
}

# ---------------------------------------------------------------- 1. game
Step 'Finding your Stardew Valley install'
if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir) { throw "Couldn't find Stardew Valley. Run again with -GameDir `"<folder containing Stardew Valley.dll>`"." }
$GameDir = (Resolve-Path $GameDir).Path.TrimEnd('\')
$dll = Join-Path $GameDir 'Stardew Valley.dll'
foreach ($required in $dll, (Join-Path $GameDir 'Content'), (Join-Path $GameDir 'MonoGame.Framework.dll')) {
    if (-not (Test-Path $required)) { throw "Not a Stardew Valley install (missing $required)." }
}
$version = (Get-Item $dll).VersionInfo.FileVersion
Write-Host "  $GameDir (version $version)"
if ($version -notlike '1.6.*') { Write-Warning "These patches were written for Stardew Valley 1.6; version $version may need updates." }

# ---------------------------------------------------------------- prerequisites
Step 'Checking prerequisites'
$sdks = dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 })) { throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download' }
Write-Host "  .NET SDK: $(($sdks | Select-Object -Last 1) -replace '\s*\[.*$', '')"
if (-not (dotnet workload list 2>$null | Select-String 'wasm-tools-net8')) {
    Write-Host '  (optional) For the fast AOT play build, install: dotnet workload install wasm-tools-net8' -ForegroundColor Yellow
}

New-Item -ItemType Directory -Force $local | Out-Null
@"
<Project>
  <!-- Written by setup.ps1. -->
  <PropertyGroup>
    <GameDir>$GameDir</GameDir>
  </PropertyGroup>
</Project>
"@ | Set-Content -Encoding utf8 (Join-Path $local 'local.props')

Push-Location $repo
try {
    Run 'Restoring tools' { dotnet tool restore | Out-Null }

    # ------------------------------------------------------------ 2. decompile
    Step 'Decompiling your copy of the game (a few minutes)'
    foreach ($dir in 'src', 'libs', 'cs6') {
        $path = Join-Path $local $dir
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Run 'Decompiling Stardew Valley.dll' { dotnet tool run ilspycmd $dll -p -o (Join-Path $local 'src') -r $GameDir | Out-Null }
    foreach ($lib in 'xTile', 'StardewValley.GameData', 'BmFont') {
        Run "Decompiling $lib.dll" { dotnet tool run ilspycmd (Join-Path $GameDir "$lib.dll") -p -o (Join-Path $local "libs\$lib") -r $GameDir | Out-Null }
    }
    Write-Host "  $((Get-ChildItem (Join-Path $local 'src') -Recurse -Filter *.cs).Count) game source files"

    # ------------------------------------------------------------ 3. patch
    Step 'Applying the browser-port patches'
    Run 'Patching' { dotnet run --project tools\PortPatcher -c Release -- --src (Join-Path $local 'src') --dll $dll --refs $GameDir --work (Join-Path $local 'cs6') }

    # ------------------------------------------------------------ 4. audio
    Step 'Exporting the game audio for Web Audio'
    Run 'Audio export' { dotnet run --project tools\AudioExport -c Release -- (Join-Path $GameDir 'Content') (Join-Path $local 'audio') }

    # ------------------------------------------------------------ 5. build
    Step 'Building'
    Run 'Build' { dotnet build web\StardewWeb.Server -v q -clp:NoSummary }
}
finally {
    Pop-Location
}

Write-Host "`nReady." -ForegroundColor Green
Write-Host '  Dev build:  dotnet run --project web\StardewWeb.Server --launch-profile StardewWeb   (http://localhost:5281)'
Write-Host '  Fast build: .\tools\publish-play.ps1  then  .\tools\play.ps1                          (http://localhost:5280)'
