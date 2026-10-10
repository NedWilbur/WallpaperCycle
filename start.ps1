[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-utils.ps1')
& (Join-Path $PSScriptRoot 'build.ps1')
$executable = Join-Path $PSScriptRoot 'bin\WallpaperCycle.exe'
Stop-AppProcess -ExecutablePath $executable
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + $executable + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
