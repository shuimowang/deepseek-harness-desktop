[CmdletBinding()]
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\DeepSeekHarness.dll'),
    [switch]$DshSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AssemblyPath = [IO.Path]::GetFullPath($AssemblyPath)
if (-not (Test-Path -LiteralPath $AssemblyPath)) {
    throw "Build output not found: $AssemblyPath"
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot "DeepSeekHarness-RecoveryTest-$([Guid]::NewGuid().ToString('N'))"
$testRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a test path outside the temporary directory: $testRoot"
}

$dshHome = Join-Path $testRoot 'dsh-home'
$recoveryRoot = Join-Path $testRoot 'recovery'
$safeHome = Join-Path $testRoot 'safe-home'
$profileDirectory = Join-Path $dshHome 'profiles\web'

try {
    New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dshHome 'sessions') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $profileDirectory 'package.json') -Value '{"state":"working"}' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $profileDirectory 'cordis.patch.yml') -Value '[]' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $dshHome 'sessions\keep.txt') -Value 'session-data' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $dshHome '.credentials.yaml') -Value 'credential-data' -Encoding utf8NoBOM

    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $type = $assembly.GetType('DshDesktop.RecoveryManager', $true)
    $flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic
    $constructor = $type.GetConstructor(
        $flags,
        $null,
        [Type[]]@([string], [string], [string]),
        $null)
    if (-not $constructor) {
        throw 'RecoveryManager test constructor not found.'
    }

    $constructorArguments = [object[]]@(
        [string]$dshHome,
        [string]$recoveryRoot,
        [string]$safeHome)
    $manager = $constructor.Invoke($constructorArguments)
    $type.GetMethod('SaveLastKnownGood').Invoke($manager, $null)

    Set-Content -LiteralPath (Join-Path $profileDirectory 'package.json') -Value '{"state":"broken"}' -Encoding utf8NoBOM
    $type.GetMethod('RestoreLastKnownGood').Invoke($manager, $null) | Out-Null

    $restored = Get-Content -LiteralPath (Join-Path $profileDirectory 'package.json') -Raw
    if ($restored -notmatch 'working') {
        throw 'The last-known-good profile was not restored.'
    }

    $incidentPackage = Get-ChildItem -LiteralPath (Join-Path $recoveryRoot 'before-restore') `
        -Filter profile-package.json -File -Recurse | Select-Object -First 1
    if (-not $incidentPackage -or (Get-Content -LiteralPath $incidentPackage.FullName -Raw) -notmatch 'broken') {
        throw 'The pre-restore profile backup was not retained.'
    }

    $type.GetMethod('PrepareSafeHome').Invoke($manager, $null) | Out-Null
    $safeManifest = Get-Content -LiteralPath (Join-Path $safeHome 'profiles\web\package.json') -Raw |
        ConvertFrom-Json
    $bundles = @($safeManifest.dsh.profile.bundles)
    if ($bundles.Count -ne 2 -or
        $bundles[0] -ne '@deepseek-ai/dsh-base' -or
        $bundles[1] -ne '@deepseek-ai/dsh-web-app') {
        throw 'Safe mode contains an unexpected bundle.'
    }

    if ((Get-Content -LiteralPath (Join-Path $dshHome 'sessions\keep.txt') -Raw).Trim() -ne 'session-data' -or
        (Get-Content -LiteralPath (Join-Path $dshHome '.credentials.yaml') -Raw).Trim() -ne 'credential-data') {
        throw 'Recovery modified session or credential data.'
    }

    Write-Host 'PASS: snapshot, restore, safe profile, and protected user data'

    if ($DshSmoke) {
        $serverType = $assembly.GetType('DshDesktop.DshServerManager', $true)
        $server = [Activator]::CreateInstance(
            $serverType,
            [object[]]@([Uri]'http://127.0.0.1:3080/'))
        try {
            $ensureTask = $serverType.GetMethod('EnsureRunningAsync').Invoke(
                $server,
                [object[]]@([string]$testRoot, [Threading.CancellationToken]::None, [string]$safeHome))
            $ensureTask.GetAwaiter().GetResult() | Out-Null

            $readyTask = $serverType.GetMethod('IsHarnessReadyAsync').Invoke(
                $server,
                [object[]]@([Threading.CancellationToken]::None))
            if (-not $readyTask.GetAwaiter().GetResult()) {
                $logs = $serverType.GetProperty('Logs').GetValue($server)
                throw "Safe DSH profile did not start through DshServerManager.`n$logs"
            }

            Write-Host 'PASS: isolated safe profile boots through DshServerManager'
        }
        finally {
            ([IDisposable]$server).Dispose()
        }
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
