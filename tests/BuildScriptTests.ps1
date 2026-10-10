[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $RepositoryRoot 'build-utils.ps1')
$fixtureRoot = Join-Path $RepositoryRoot ('data\test-runs\build-script-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$testCount = 0

function Assert-Result {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function New-EmptyFile {
    param([string]$Path, [DateTime]$Time)
    [System.IO.File]::WriteAllText($Path, 'test fixture')
    [System.IO.File]::SetLastWriteTimeUtc($Path, $Time)
}

try {
    $inputSource = Join-Path $fixtureRoot 'input.cs'
    $inputManifest = Join-Path $fixtureRoot 'app.manifest'
    $outputExe = Join-Path $fixtureRoot 'WallpaperCycle.exe'
    $outputConfig = Join-Path $fixtureRoot 'WallpaperCycle.exe.config'
    $old = [DateTime]::SpecifyKind([DateTime]'2026-01-01T00:00:00', [DateTimeKind]::Utc)
    $new = $old.AddMinutes(1)
    New-EmptyFile $inputSource $old
    New-EmptyFile $inputManifest $new
    New-EmptyFile $outputExe $new
    New-EmptyFile $outputConfig $new

    Assert-Result (Test-BuildCurrent -CompileInputPaths @($inputSource, $inputManifest) -ExecutablePath $outputExe -RuntimeConfigSource $inputManifest -RuntimeConfigOutput $outputConfig) 'A current build should be reusable while the app runs.'
    $testCount++
    Write-Output 'PASS current build outputs skip recompilation'

    [System.IO.File]::SetLastWriteTimeUtc($outputConfig, $old)
    Assert-Result (-not (Test-BuildCurrent -CompileInputPaths @($inputSource) -ExecutablePath $outputExe -RuntimeConfigSource $inputManifest -RuntimeConfigOutput $outputConfig)) 'An older runtime config must require a build.'
    $testCount++
    Write-Output 'PASS stale runtime config requires a rebuild'

    [System.IO.File]::SetLastWriteTimeUtc($outputConfig, $new)
    [System.IO.File]::SetLastWriteTimeUtc($outputExe, $old)
    Assert-Result (-not (Test-BuildCurrent -CompileInputPaths @($inputSource, $inputManifest) -ExecutablePath $outputExe -RuntimeConfigSource $inputManifest -RuntimeConfigOutput $outputConfig)) 'An older executable must require a build.'
    $testCount++
    Write-Output 'PASS stale executable requires a rebuild'

    Assert-Result (-not (Test-BuildCurrent -CompileInputPaths @($inputSource, (Join-Path $fixtureRoot 'missing.cs')) -ExecutablePath $outputExe -RuntimeConfigSource $inputManifest -RuntimeConfigOutput $outputConfig)) 'A missing source file must require a build.'
    $testCount++
    Write-Output 'PASS missing input requires a build'

    $expectedExe = Join-Path $fixtureRoot 'bin\WallpaperCycle.exe'
    $matchingProcess = [pscustomobject]@{ Path = $expectedExe }
    Assert-Result (Test-AppProcessRunning -ExecutablePath $expectedExe -Processes @($matchingProcess)) 'An active app using the output executable must be detected.'
    $testCount++
    Write-Output 'PASS active app is detected before a stale rebuild'

    $otherProcess = [pscustomobject]@{ Path = (Join-Path $fixtureRoot 'other\WallpaperCycle.exe') }
    Assert-Result (-not (Test-AppProcessRunning -ExecutablePath $expectedExe -Processes @($otherProcess))) 'Another checkout must not block a build.'
    $testCount++
    Write-Output 'PASS another checkout does not block a build'

    $processWithNoReadablePath = [pscustomobject]@{ Path = $null }
    Assert-Result (-not (Test-AppProcessRunning -ExecutablePath $expectedExe -Processes @($processWithNoReadablePath))) 'An unrelated process without an executable path must not block a build.'
    $testCount++
    Write-Output 'PASS unrelated processes do not block a build'

    Write-Output ("{0} PowerShell build tests passed." -f $testCount)
}
finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'data\test-runs'))
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedFixture)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
