[CmdletBinding()]
param(
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-utils.ps1')
$outputDirectory = Join-Path $PSScriptRoot 'bin'
$outputFile = Join-Path $outputDirectory 'WallpaperCycle.exe'
$outputConfig = Join-Path $outputDirectory 'WallpaperCycle.exe.config'
$sourceDirectory = Join-Path $PSScriptRoot 'src'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
$compileInputs = @($sources) + @(Join-Path $PSScriptRoot 'app.manifest')
$runtimeConfigSource = Join-Path $PSScriptRoot 'WallpaperCycle.exe.config'
if (-not $Force -and (Test-BuildCurrent -CompileInputPaths $compileInputs -ExecutablePath $outputFile -RuntimeConfigSource $runtimeConfigSource -RuntimeConfigOutput $outputConfig)) {
    Write-Output ('Build is up to date: ' + $outputFile)
    return
}
$runningApps = @(Get-Process -Name 'WallpaperCycle' -ErrorAction SilentlyContinue)
if (Test-AppProcessRunning -ExecutablePath $outputFile -Processes $runningApps) {
    throw 'WallpaperCycle is running. Choose Exit from its tray menu, then rerun this build or install script.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework C# compiler was not found. Install .NET Framework 4.8.'
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compilerArguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', '/warnaserror+',
    ('/out:' + $outputFile),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Net.Http.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll'
)
& $compiler @compilerArguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath $runtimeConfigSource -Destination $outputConfig -Force
Write-Output ('Built ' + $outputFile)
