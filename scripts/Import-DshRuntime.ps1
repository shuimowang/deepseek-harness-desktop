[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot,
    [Parameter(Mandatory)]
    [string]$UpstreamTag,
    [Parameter(Mandatory)]
    [string]$UpstreamCommit,
    [Parameter(Mandatory)]
    [string]$HarnessVersion,
    [string]$DesktopVersion = '1.9.1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$runtimeRoot = Join-Path $projectRoot 'runtime\dsh-package'
$packagesRoot = Join-Path $runtimeRoot 'packages'
$projectPrefix = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $runtimeRoot.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to update a runtime path outside the project: $runtimeRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot '.git'))) {
    throw "The source directory is not a Git checkout: $sourceRoot"
}

$actualCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $UpstreamCommit) {
    throw "Expected upstream commit $UpstreamCommit, found $actualCommit"
}
$tagCommit = (& git -C $sourceRoot rev-list -n 1 $UpstreamTag).Trim()
if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $UpstreamCommit) {
    throw "Tag $UpstreamTag does not resolve to $UpstreamCommit"
}

$packRoots = @(
    (Join-Path $sourceRoot 'dist\npm'),
    (Join-Path $sourceRoot 'dist\npm-vendor'),
    (Join-Path $sourceRoot 'dist\npm-landlock')
)
$tarballs = @($packRoots | ForEach-Object {
    if (-not (Test-Path -LiteralPath $_)) {
        throw "Packed output is missing: $_"
    }
    Get-ChildItem -LiteralPath $_ -Filter '*.tgz' -File
} | Sort-Object Name)
if ($tarballs.Count -eq 0) {
    throw 'No packed tarballs were found.'
}

$nativeProvenance = $null
# Older Harness releases use fs-ext without a Windows prebuild. If the
# current upstream release no longer carries it, keep the runtime unchanged.
if ((Get-Content -LiteralPath (Join-Path $sourceRoot 'packages\session\session-persistence-jsonl\package.json') -Raw) -match '"fs-ext"\s*:') {
# Ship the binding compiled with the bundled Node ABI so end users never need
# Python or Visual Studio.
$nativeNodeVersion = (& node --version).Trim().TrimStart('v')
$nativePlatform = (& node -p "process.platform + '-' + process.arch").Trim()
$nativeAbi = (& node -p 'process.versions.modules').Trim()
if ($nativePlatform -ne 'win32-x64' -or $nativeNodeVersion -ne '24.14.1') {
    throw 'Import requires Windows x64 and Node.js 24.14.1 for the fs-ext binding.'
}
$fsExtRoot = (& node -p "require('node:path').dirname(require.resolve('fs-ext/package.json', { paths: [process.argv[1]] }))" `
    (Join-Path $sourceRoot 'packages\session\session-persistence-jsonl')).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the upstream fs-ext installation.' }
$fsExtPackage = Get-Content -LiteralPath (Join-Path $fsExtRoot 'package.json') -Raw | ConvertFrom-Json
if ($fsExtPackage.version -ne '2.1.1') { throw 'Review the new fs-ext version before importing.' }
& node -e "require(process.argv[1]); console.log('fs-ext native binding loaded')" $fsExtRoot
if ($LASTEXITCODE -ne 0) { throw 'The compiled fs-ext binding cannot load.' }
$nativeStage = Join-Path $projectRoot ".build-cache\fs-ext-prebuilt-$([Guid]::NewGuid().ToString('N'))"
$nativePackageRoot = Join-Path $nativeStage 'package'
New-Item -ItemType Directory -Path (Join-Path $nativePackageRoot 'build\Release') -Force | Out-Null
foreach ($name in 'fs-ext.js','fs-ext.cc','binding.gyp','LICENSE.txt','README.md') {
    Copy-Item -LiteralPath (Join-Path $fsExtRoot $name) -Destination $nativePackageRoot
}
Copy-Item -LiteralPath (Join-Path $fsExtRoot 'build\Release\fs_ext.node') `
    -Destination (Join-Path $nativePackageRoot 'build\Release\fs_ext.node')
$fsExtPackage.scripts = [pscustomobject]@{}
$fsExtPackage | Add-Member -NotePropertyName gypfile -NotePropertyValue $false -Force
$fsExtPackage | Add-Member -NotePropertyName os -NotePropertyValue @('win32') -Force
$fsExtPackage | Add-Member -NotePropertyName cpu -NotePropertyValue @('x64') -Force
$fsExtPackage.engines.node = '>=24.0.0 <25.0.0'
$fsExtPackage | ConvertTo-Json -Depth 10 | Set-Content `
    -LiteralPath (Join-Path $nativePackageRoot 'package.json') -Encoding utf8NoBOM
$nativeProvenance = [ordered]@{
    package = 'fs-ext'
    version = '2.1.1'
    source = 'https://registry.npmjs.org/fs-ext/-/fs-ext-2.1.1.tgz'
    nodeVersion = $nativeNodeVersion
    nodeAbi = $nativeAbi
    platform = $nativePlatform
    bindingSha256 = (Get-FileHash -LiteralPath (Join-Path $nativePackageRoot 'build\Release\fs_ext.node')).Hash
    changes = 'Unmodified binding and JS; precompiled Windows x64 binary; install scripts disabled.'
}
$nativeProvenance | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $nativePackageRoot 'PREBUILD.json') -Encoding utf8NoBOM
$nativeArchive = Join-Path $nativeStage 'fs-ext-2.1.1-win32-x64-node24.tgz'
& tar -czf $nativeArchive -C $nativeStage package
if ($LASTEXITCODE -ne 0) { throw 'Cannot pack the fs-ext native runtime.' }
$tarballs += Get-Item -LiteralPath $nativeArchive
}

if (Test-Path -LiteralPath $packagesRoot) {
    Remove-Item -LiteralPath $packagesRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null
foreach ($oldLock in 'package-lock.json','pnpm-lock.yaml','pnpm-workspace.yaml') {
    $oldLockPath = Join-Path $runtimeRoot $oldLock
    if (Test-Path -LiteralPath $oldLockPath) {
        Remove-Item -LiteralPath $oldLockPath -Force
    }
}

$packageRecords = @{}
foreach ($tarball in $tarballs) {
    $packageText = & tar -xOf $tarball.FullName package/package.json
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read package.json from $($tarball.FullName)"
    }
    $package = $packageText | ConvertFrom-Json
    if ($packageRecords.ContainsKey($package.name)) {
        throw "Duplicate package in packed outputs: $($package.name)"
    }
    $packageRecords[$package.name] = [pscustomobject]@{
        Tarball = $tarball
        Package = $package
    }
}

$dshRecord = $packageRecords['@deepseek-ai/dsh']
if (-not $dshRecord) {
    throw 'The packed outputs do not contain @deepseek-ai/dsh.'
}
if ($dshRecord.Package.version -ne $HarnessVersion) {
    throw "Packed DSH version is $($dshRecord.Package.version), expected $HarnessVersion"
}

$reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue('@deepseek-ai/dsh')
while ($pending.Count -gt 0) {
    $packageName = $pending.Dequeue()
    if (-not $reachable.Add($packageName)) {
        continue
    }

    $package = $packageRecords[$packageName].Package
    foreach ($field in 'dependencies','optionalDependencies','peerDependencies') {
        if ($package.PSObject.Properties.Name -notcontains $field) {
            continue
        }
        foreach ($dependencyProperty in $package.$field.PSObject.Properties) {
            $dependencyName = $dependencyProperty.Name
            if ($packageRecords.ContainsKey($dependencyName) -and -not $reachable.Contains($dependencyName)) {
                $pending.Enqueue($dependencyName)
            }
        }
    }
}

$dependencies = [ordered]@{}
foreach ($packageName in $reachable | Sort-Object) {
    $record = $packageRecords[$packageName]
    Copy-Item -LiteralPath $record.Tarball.FullName -Destination $packagesRoot
    $dependencies[$packageName] = "file:packages/$($record.Tarball.Name)"
}
$dshSpec = $dependencies['@deepseek-ai/dsh']

[ordered]@{
    name = 'deepseek-harness-desktop-runtime'
    version = $DesktopVersion
    private = $true
    license = 'MIT'
    packageManager = 'pnpm@11.7.0'
    dependencies = $dependencies
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runtimeRoot 'package.json') -Encoding utf8NoBOM

[ordered]@{
    upstreamTag = $UpstreamTag
    upstreamCommit = $UpstreamCommit
    harnessVersion = $HarnessVersion
    packageCount = $dependencies.Count
    nativePrebuild = $nativeProvenance
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtimeRoot 'SOURCE.json') -Encoding utf8NoBOM

$workspaceLines = [Collections.Generic.List[string]]::new()
$workspaceLines.Add('allowBuilds:')
$workspaceLines.Add("  '@deepseek-ai/dsh-subprocess-local@$($dependencies['@deepseek-ai/dsh-subprocess-local'])': true")
$workspaceLines.Add("  '@google/genai': false")
$workspaceLines.Add('  esbuild: true')
$workspaceLines.Add('  koffi: true')
$workspaceLines.Add('  node-pty: true')
$workspaceLines.Add('  protobufjs: false')
$workspaceLines.Add('  node-addon-require-builtin: false')
if ($null -ne $nativeProvenance) {
    $workspaceLines.Add('  fs-ext: false')
}
$workspaceLines.Add('overrides:')
foreach ($dependency in $dependencies.GetEnumerator()) {
    $name = ([string]$dependency.Key).Replace("'", "''")
    $value = ([string]$dependency.Value).Replace("'", "''")
    $workspaceLines.Add("  '$name': '$value'")
}
$workspaceLines.Add("  'react': '18.3.1'")
$workspaceLines.Add("  'react-dom': '18.3.1'")
$workspaceLines | Set-Content `
    -LiteralPath (Join-Path $runtimeRoot 'pnpm-workspace.yaml') `
    -Encoding utf8NoBOM

Push-Location $runtimeRoot
try {
    & corepack pnpm install `
        --lockfile-only `
        --ignore-scripts `
        --prod
    $pnpmExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($pnpmExitCode -ne 0) {
    throw "pnpm lock generation failed with exit code $pnpmExitCode"
}

$lockText = Get-Content -LiteralPath (Join-Path $runtimeRoot 'pnpm-lock.yaml') -Raw
if (-not $lockText.Contains($dshSpec, [StringComparison]::Ordinal)) {
    throw "Generated pnpm lock does not contain $dshSpec"
}

Write-Host "Imported $($dependencies.Count) tarballs from $UpstreamTag ($UpstreamCommit)."
Write-Host "Locked @deepseek-ai/dsh@$HarnessVersion with pnpm 11.7.0."
