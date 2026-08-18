[CmdletBinding()]
param(
    [switch]$Launch,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$exePath = @(
    (Join-Path $PSScriptRoot 'DeepSeekHarness.exe'),
    (Join-Path $PSScriptRoot 'dist\DeepSeekHarness\DeepSeekHarness.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

$shortcutTargets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk'),
    (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\DeepSeek Harness.lnk')
)

if ($Remove) {
    foreach ($shortcutPath in $shortcutTargets) {
        if (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath -Force
            Write-Host "Removed: $shortcutPath"
        }
    }
    return
}

if (-not $exePath) {
    throw 'Desktop client not found. Keep this script beside DeepSeekHarness.exe or run dotnet publish first.'
}

$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in $shortcutTargets) {
    $directory = Split-Path $shortcutPath -Parent
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = Split-Path $exePath -Parent
    $shortcut.IconLocation = "$exePath,0"
    $shortcut.Description = 'Launch DeepSeek Harness Desktop'
    $shortcut.Save()
    Write-Host "Created: $shortcutPath"
}

if ($Launch) {
    Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath -Parent)
}
