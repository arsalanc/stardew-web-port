# Runs the AOT "play" build (from tools/publish-play.ps1) on http://localhost:5280,
# installing a newer staged build first if one is waiting.
# Port 5280 matters: browser saves are stored per address, so this is where your saves live.
# (The Debug dev server uses 5281.)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'play-common.ps1')

if (Install-StagedPlayBuild) { 'Installed the new play build.' }

$server = Join-Path $PlayDir 'StardewWeb.Server.exe'
if (-not (Test-Path (Join-Path $PlayDir 'wwwroot\index.html'))) { throw "No complete play build. Run tools\publish-play.ps1 first." }
Push-Location $PlayDir
try { & $server --urls http://localhost:5280 }
finally { Pop-Location }
