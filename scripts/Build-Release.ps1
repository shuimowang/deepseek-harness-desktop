[CmdletBinding()]
param(
    [string]$Version = '1.1.0',
    [string]$NodeVersion = '24.14.1',
    [string]$HarnessVersion = '0.1.0-rc.7',
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
$packageSuffix = if ($OnlineLite) { '-online' } elseif ($SkipRuntimeBundle) { '-client-only' } else { '' }
$packageName = "DeepSeekHarness-$Version-win-x64$packageSuffix"
$packageDirectory = Join-Path $artifactRoot $packageName
$workDirectory = Join-Path $artifactRoot '.release-work'
$zipPath = Join-Path $artifactRoot "$packageName.zip"

function Assert-ProjectChildPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the project: $resolved"
    }
}

function Get-NodeExpectedHash([string]$RequestedNodeVersion, [string]$ArchiveName) {
    $nodeBaseUrl = "https://nodejs.org/dist/v$RequestedNodeVersion"
    $checksumText = (Invoke-WebRequest -UseBasicParsing -Uri "$nodeBaseUrl/SHASUMS256.txt").Content
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
& dotnet publish (Join-Path $projectRoot 'DshDesktop.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $packageDirectory `
    -p:Version=$Version `
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
    $cacheDirectory = Join-Path $artifactRoot 'cache'
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
    $npmCommand = Join-Path $nodeDestination 'npm.cmd'
    $previousPath = $env:PATH
    try {
        $env:PATH = "$nodeDestination;$previousPath"
        Write-Host "Installing @deepseek-ai/dsh@$HarnessVersion..."
        & $npmCommand install `
            --prefix $dshDestination `
            --save-exact `
            "@deepseek-ai/dsh@$HarnessVersion" `
            --omit=dev `
            --no-audit `
            --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        $env:PATH = $previousPath
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

Write-Host "Created: $zipPath"
Write-Host "SHA-256: $zipHash"
