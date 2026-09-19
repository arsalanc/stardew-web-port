# Shared helpers for publish-play.ps1 and play.ps1.
$repo = Split-Path $PSScriptRoot -Parent
$PlayDir = Join-Path $repo 'publish'
$StagingDir = Join-Path $repo 'publish-staging'
$ReadyMarker = Join-Path $StagingDir '.ready'

function Test-PlayServerRunning {
	$exe = Join-Path $PlayDir 'StardewWeb.Server.exe'
	[bool](Get-Process StardewWeb.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
}

# Moves a finished staging build into publish/. Uses renames only: a rename either fully succeeds
# or fails without touching anything, so a locked file can never leave a half-deleted build.
function Install-StagedPlayBuild {
	if (-not (Test-Path $ReadyMarker)) { return $false }
	if (Test-PlayServerRunning) { throw 'The play server is running; stop play.ps1 before installing a new build.' }
	$old = Join-Path $repo 'publish-old'
	if (Test-Path $old) { Remove-Item $old -Recurse -Force }
	if (Test-Path $PlayDir) { Rename-Item $PlayDir 'publish-old' -ErrorAction Stop }
	try {
		Rename-Item $StagingDir 'publish' -ErrorAction Stop
	}
	catch {
		# Put the previous build back so there's always a runnable one.
		if (Test-Path $old) { Rename-Item $old 'publish' }
		throw
	}
	Remove-Item (Join-Path $PlayDir '.ready') -Force
	if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
	return $true
}
