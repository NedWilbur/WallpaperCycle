function Test-BuildCurrent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$CompileInputPaths,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$RuntimeConfigSource,
        [Parameter(Mandatory = $true)][string]$RuntimeConfigOutput
    )

    $paths = @($CompileInputPaths) + @($ExecutablePath, $RuntimeConfigSource, $RuntimeConfigOutput)
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    }
    $newestCompileInput = Get-Item -LiteralPath $CompileInputPaths | Sort-Object -Property LastWriteTimeUtc -Descending | Select-Object -First 1
    $executable = Get-Item -LiteralPath $ExecutablePath
    $configSource = Get-Item -LiteralPath $RuntimeConfigSource
    $configOutput = Get-Item -LiteralPath $RuntimeConfigOutput
    return ($executable.LastWriteTimeUtc -ge $newestCompileInput.LastWriteTimeUtc) -and
        ($configOutput.LastWriteTimeUtc -ge $configSource.LastWriteTimeUtc)
}

function Test-AppProcessRunning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [AllowEmptyCollection()][object[]]$Processes = @()
    )

    $expected = [System.IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in $Processes) {
        try {
            if ([string]::Equals(
                [System.IO.Path]::GetFullPath([string]$process.Path),
                $expected,
                [StringComparison]::OrdinalIgnoreCase
            )) { return $true }
        }
        catch { }
    }
    return $false
}

function Stop-AppProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $expected = [System.IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in @(Get-Process -Name 'WallpaperCycle' -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try { $processPath = [System.IO.Path]::GetFullPath([string]$process.Path) }
        catch { continue }
        if ([string]::Equals($processPath, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force
            Wait-Process -Id $process.Id -ErrorAction SilentlyContinue
        }
    }
}
