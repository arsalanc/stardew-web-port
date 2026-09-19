# Builds the AOT-compiled "play" version of the web port.
# Slow (AOT compiles the whole game to WebAssembly); only needed after code changes.
# Safe to run while play.ps1 is running: the new build waits in publish-staging/ and is
# installed the next time play.ps1 starts (or immediately if the play server isn't running).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'play-common.ps1')

$sw = [Diagnostics.Stopwatch]::StartNew()
if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
dotnet publish (Join-Path $repo 'web\StardewWeb.Server') -c Release -o $StagingDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
New-Item -ItemType File $ReadyMarker | Out-Null
$minutes = '{0:N1}' -f $sw.Elapsed.TotalMinutes

if (Test-PlayServerRunning) {
	"Play build ready in $minutes min. The play server is running, so it'll switch over the next time you start tools\play.ps1."
}
else {
	Install-StagedPlayBuild | Out-Null
	"Play build ready in $minutes min and installed. Start it with tools\play.ps1."
}
