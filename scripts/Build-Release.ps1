[CmdletBinding()]
param(
    [string]$Version = '1.9.0',
    [string]$NodeVersion = '24.14.1',
    [string]$HarnessVersion = '0.1.7-alpha.2',
    [switch]$OnlineLite,
    [switch]$SkipRuntimeBundle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($OnlineLite -and $SkipRuntimeBundle) {
    throw '-OnlineLite and -SkipRuntimeBundle cannot be used together.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$buildCacheRoot = Join-Path $projectRoot '.build-cache'
$packageSuffix = if ($OnlineLite) { '-online' } elseif ($SkipRuntimeBundle) { '-client-only' } else { '' }
$packageName = "DeepSeekHarness-$Version-win-x64$packageSuffix"
$packageDirectory = Join-Path $artifactRoot $packageName
$workDirectory = Join-Path $artifactRoot '.release-work'
$zipPath = Join-Path $artifactRoot "$packageName.zip"
$runtimePackageDirectory = Join-Path $projectRoot 'runtime\dsh-package'
$runtimePackageJson = Join-Path $runtimePackageDirectory 'package.json'
$runtimePnpmLock = Join-Path $runtimePackageDirectory 'pnpm-lock.yaml'
$runtimePnpmWorkspace = Join-Path $runtimePackageDirectory 'pnpm-workspace.yaml'
$runtimePackageSource = Join-Path $runtimePackageDirectory 'SOURCE.json'
$runtimePackageArchives = Join-Path $runtimePackageDirectory 'packages'

if (-not (Test-Path -LiteralPath $runtimePackageJson) -or
    -not (Test-Path -LiteralPath $runtimePnpmLock) -or
    -not (Test-Path -LiteralPath $runtimePnpmWorkspace) -or
    -not (Test-Path -LiteralPath $runtimePackageSource) -or
    -not (Test-Path -LiteralPath $runtimePackageArchives)) {
    throw 'The locked DSH runtime package files are missing.'
}

$runtimePackage = Get-Content -LiteralPath $runtimePackageJson -Raw | ConvertFrom-Json
$runtimeSource = Get-Content -LiteralPath $runtimePackageSource -Raw | ConvertFrom-Json
$harnessPackageSpec = $runtimePackage.dependencies.'@deepseek-ai/dsh'
$lockedHarnessVersion = $runtimeSource.harnessVersion
if ($null -ne $runtimeSource.nativePrebuild -and $runtimeSource.nativePrebuild.nodeVersion -ne $NodeVersion) {
    throw "Native binding targets Node.js $($runtimeSource.nativePrebuild.nodeVersion), not $NodeVersion."
}
if ($harnessPackageSpec -notmatch '^file:packages/' -or
    $harnessPackageSpec -notmatch [regex]::Escape($HarnessVersion) -or
    $runtimePackage.packageManager -ne 'pnpm@11.7.0' -or
    $lockedHarnessVersion -ne $HarnessVersion -or
    $runtimeSource.harnessVersion -ne $HarnessVersion -or
    $runtimeSource.upstreamCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Runtime package locks DSH $lockedHarnessVersion, but the build requests $HarnessVersion."
}

$runtimePrefix = $runtimePackageDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($dependency in $runtimePackage.dependencies.PSObject.Properties) {
    $packageSpec = [string]$dependency.Value
    if ($packageSpec -notmatch '^file:packages/[^/]+\.tgz$') {
        throw "Runtime dependency $($dependency.Name) is not a local package tarball: $packageSpec"
    }
    $archivePath = [IO.Path]::GetFullPath((Join-Path $runtimePackageDirectory $packageSpec.Substring('file:'.Length)))
    if (-not $archivePath.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $archivePath)) {
        throw "Runtime dependency archive is missing or outside the package directory: $archivePath"
    }
}

function Assert-ProjectChildPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the project: $resolved"
    }
}

function Get-NodeExpectedHash([string]$RequestedNodeVersion, [string]$ArchiveName) {
    $nodeBaseUrl = "https://nodejs.org/dist/v$RequestedNodeVersion"
    $checksumUri = "$nodeBaseUrl/SHASUMS256.txt"
    $checksumText = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $checksumText = (Invoke-WebRequest -UseBasicParsing -Uri $checksumUri).Content
            break
        }
        catch {
            Write-Warning "Node.js checksum request failed (attempt $attempt of 3): $($_.Exception.Message)"
            if ($attempt -lt 3) {
                Start-Sleep -Seconds $attempt
            }
        }
    }
    if (-not $checksumText) {
        $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if (-not $curl) {
            throw "Unable to download Node.js checksums from $checksumUri"
        }
        $checksumText = (& $curl.Source -fsSL --retry 3 --retry-all-errors $checksumUri) -join "`n"
        if ($LASTEXITCODE -ne 0 -or -not $checksumText) {
            throw "Unable to download Node.js checksums from $checksumUri"
        }
    }
    $checksumLine = $checksumText -split "`n" |
        Where-Object { $_ -match "\s+$([regex]::Escape($ArchiveName))\s*$" } |
        Select-Object -First 1
    if (-not $checksumLine) {
        throw "Node.js checksum entry not found for $ArchiveName"
    }

    return ($checksumLine.Trim() -split '\s+')[0].ToUpperInvariant()
}

foreach ($target in @($packageDirectory, $workDirectory, $zipPath, "$zipPath.sha256")) {
    Assert-ProjectChildPath $target
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null

Write-Host "Publishing $packageName..."
$includeOnlineRuntimePackage = if ($OnlineLite) { 'true' } else { 'false' }
& dotnet publish (Join-Path $projectRoot 'DshDesktop.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $packageDirectory `
    -p:Version=$Version `
    -p:IncludeOnlineRuntimePackage=$includeOnlineRuntimePackage `
    -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'Install-DesktopShortcut.ps1') -Destination $packageDirectory

$licenseDirectory = Join-Path $packageDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
$webViewPackage = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2\1.0.4129.50'
Copy-Item -LiteralPath (Join-Path $webViewPackage 'LICENSE.txt') `
    -Destination (Join-Path $licenseDirectory 'WebView2-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $webViewPackage 'NOTICE.txt') `
    -Destination (Join-Path $licenseDirectory 'WebView2-NOTICE.txt')

if ($OnlineLite) {
    $nodeArchiveName = "node-v$NodeVersion-win-x64.zip"
    $expectedNodeHash = Get-NodeExpectedHash $NodeVersion $nodeArchiveName
    [ordered]@{
        mode = 'online'
        nodeVersion = $NodeVersion
        nodeArchiveSha256 = $expectedNodeHash
        harnessVersion = $HarnessVersion
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'runtime-mode.json') -Encoding utf8
}
elseif (-not $SkipRuntimeBundle) {
    $cacheDirectory = $buildCacheRoot
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null

    $nodeArchiveName = "node-v$NodeVersion-win-x64.zip"
    $nodeArchive = Join-Path $cacheDirectory $nodeArchiveName
    $nodeBaseUrl = "https://nodejs.org/dist/v$NodeVersion"
    $expectedNodeHash = Get-NodeExpectedHash $NodeVersion $nodeArchiveName
    $downloadNode = -not (Test-Path -LiteralPath $nodeArchive)
    if (-not $downloadNode) {
        $downloadNode = (Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash -ne $expectedNodeHash
    }

    if ($downloadNode) {
        Write-Host "Downloading Node.js $NodeVersion..."
        Invoke-WebRequest -UseBasicParsing -Uri "$nodeBaseUrl/$nodeArchiveName" -OutFile $nodeArchive
    }

    $actualNodeHash = (Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash
    if ($actualNodeHash -ne $expectedNodeHash) {
        throw "Node.js archive checksum mismatch. Expected $expectedNodeHash, got $actualNodeHash"
    }

    $nodeExtractDirectory = Join-Path $workDirectory 'node'
    Expand-Archive -LiteralPath $nodeArchive -DestinationPath $nodeExtractDirectory
    $nodeSourceDirectory = Join-Path $nodeExtractDirectory "node-v$NodeVersion-win-x64"
    $nodeDestination = Join-Path $packageDirectory 'runtime\node'
    New-Item -ItemType Directory -Path $nodeDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $nodeSourceDirectory '*') -Destination $nodeDestination -Recurse

    $dshDestination = Join-Path $packageDirectory 'runtime\dsh'
    New-Item -ItemType Directory -Path $dshDestination -Force | Out-Null
    Copy-Item -LiteralPath $runtimePackageJson -Destination $dshDestination
    Copy-Item -LiteralPath $runtimePnpmLock -Destination $dshDestination
    Copy-Item -LiteralPath $runtimePnpmWorkspace -Destination $dshDestination
    Copy-Item -LiteralPath $runtimePackageSource -Destination $dshDestination
    Copy-Item -LiteralPath $runtimePackageArchives `
        -Destination $dshDestination -Recurse
    $pnpmStoreDirectory = Join-Path $buildCacheRoot 'pnpm-store'
    $corepackCacheDirectory = Join-Path $buildCacheRoot 'corepack'
    New-Item -ItemType Directory -Path $pnpmStoreDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $corepackCacheDirectory -Force | Out-Null
    $nodeCommand = Join-Path $nodeDestination 'node.exe'
    $corepackCli = Join-Path $nodeDestination 'node_modules\corepack\dist\corepack.js'
    $previousPath = $env:PATH
    $previousCorepackHome = $env:COREPACK_HOME
    $previousPnpmHome = $env:PNPM_HOME
    try {
        $env:PATH = "$nodeDestination;$previousPath"
        $env:COREPACK_HOME = $corepackCacheDirectory
        $env:PNPM_HOME = Join-Path $buildCacheRoot 'pnpm-home'
        Write-Host "Installing locked @deepseek-ai/dsh@$HarnessVersion runtime..."
        Push-Location $dshDestination
        & $nodeCommand $corepackCli pnpm install `
            --frozen-lockfile `
            --prod `
            --reporter append-only `
            --config.node-linker=hoisted `
            --config.package-import-method=copy `
            --store-dir $pnpmStoreDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "pnpm install failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        if ((Get-Location).Path -eq $dshDestination) {
            Pop-Location
        }
        $env:PATH = $previousPath
        $env:COREPACK_HOME = $previousCorepackHome
        $env:PNPM_HOME = $previousPnpmHome
    }

    [ordered]@{
        application = "DeepSeek Harness Desktop"
        version = $Version
        architecture = 'win-x64'
        node = $NodeVersion
        deepSeekHarness = $HarnessVersion
        builtAtUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'runtime-manifest.json') -Encoding utf8
}

Write-Host 'Creating portable ZIP...'
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash *$([IO.Path]::GetFileName($zipPath))" |
    Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii

Remove-Item -LiteralPath $workDirectory -Recurse -Force
& (Join-Path $PSScriptRoot 'Cleanup-Artifacts.ps1') -KeepVersion $Version

Write-Host "Created: $zipPath"
Write-Host "SHA-256: $zipHash"
