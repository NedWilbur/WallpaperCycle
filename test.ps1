[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$outputDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sources = @('src\Settings.cs', 'src\Wallhaven.cs', 'src\Rotation.cs', 'src\WindowsIntegration.cs', 'src\TrayIcon.cs', 'src\BackgroundPolicy.cs', 'src\FavoriteStore.cs', 'src\Program.cs', 'tests\TestRunner.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$arguments = @(
    '/nologo', '/target:exe', '/main:TestRunner', '/warn:4', '/warnaserror+',
    ('/out:' + (Join-Path $outputDirectory 'WallpaperCycle.Tests.exe')),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Net.Http.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll'
)
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WallpaperCycle.exe.config') -Destination (Join-Path $outputDirectory 'WallpaperCycle.Tests.exe.config') -Force
& (Join-Path $outputDirectory 'WallpaperCycle.Tests.exe') $PSScriptRoot
$applicationTestExitCode = $LASTEXITCODE
& (Join-Path $PSScriptRoot 'tests\BuildScriptTests.ps1') -RepositoryRoot $PSScriptRoot
$buildTestExitCode = $LASTEXITCODE
if ($applicationTestExitCode -ne 0 -or $buildTestExitCode -ne 0) { throw 'One or more test suites failed.' }
