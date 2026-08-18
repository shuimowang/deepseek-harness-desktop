[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [int]$StartupTimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $PSScriptRoot '..\dist\DeepSeekHarness\DeepSeekHarness.exe'
}

$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Executable not found: $ExecutablePath"
}

$existing = @(Get-Process -Name DeepSeekHarness -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw 'Close all existing DeepSeek Harness Desktop windows before running the smoke test.'
}

$primary = $null
try {
    $primary = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath -Parent) -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 400
        $primary.Refresh()
    } while ((Get-Date) -lt $deadline -and -not $primary.HasExited -and $primary.MainWindowHandle -eq 0)

    if ($primary.HasExited -or $primary.MainWindowHandle -eq 0) {
        throw 'The primary window did not become ready.'
    }

    $secondary = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath -Parent) -PassThru
    if (-not $secondary.WaitForExit(8000)) {
        throw 'A second application instance remained running.'
    }

    $runningInstances = @(Get-Process -Name DeepSeekHarness -ErrorAction SilentlyContinue)
    if ($runningInstances.Count -ne 1 -or $runningInstances[0].Id -ne $primary.Id) {
        throw "Expected one primary instance; found $($runningInstances.Count)."
    }

    Write-Host 'PASS: startup and single-instance activation'
}
finally {
    if ($primary -and -not $primary.HasExited) {
        $primary.CloseMainWindow() | Out-Null
        if (-not $primary.WaitForExit(10000)) {
            Stop-Process -Id $primary.Id -Force
        }
    }
}
