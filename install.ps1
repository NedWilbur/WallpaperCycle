[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-utils.ps1')
& (Join-Path $PSScriptRoot 'build.ps1')
$executable = Join-Path $PSScriptRoot 'bin\WallpaperCycle.exe'
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not (Test-Path -LiteralPath $startupKey)) {
    New-Item -Path $startupKey | Out-Null
}
New-ItemProperty -Path $startupKey -Name 'WallpaperCycle' -Value ('"' + $executable + '"') -PropertyType String -Force | Out-Null
Stop-AppProcess -ExecutablePath $executable
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + $executable + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
Write-Output 'WallpaperCycle is running and will start at Windows sign-in.'
Write-Output 'No API key is needed for default SFW wallpapers.'
Write-Output ('Logs: ' + (Join-Path $PSScriptRoot 'data\app.log'))
