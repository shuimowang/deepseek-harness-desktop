[CmdletBinding()]
param(
    [string]$KeepVersion,
    [switch]$KeepUnpacked
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
    Write-Host "Artifacts directory does not exist: $artifactRoot"
    return
}

if (-not $KeepVersion) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'DshDesktop.csproj') -Raw
    $KeepVersion = [string]$project.Project.PropertyGroup.Version
}
if ($KeepVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid version to keep: $KeepVersion"
}

$releasePattern = '^DeepSeekHarness-(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)-win-x64(?:-online|-client-only)?(?:\.zip(?:\.sha256)?)?$'
$notesPattern = '^release-notes-v(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\.md$'
$targets = [Collections.Generic.List[IO.FileSystemInfo]]::new()

foreach ($item in Get-ChildItem -LiteralPath $artifactRoot -Force) {
    $match = [regex]::Match($item.Name, $releasePattern)
    if (-not $match.Success) {
        $match = [regex]::Match($item.Name, $notesPattern)
    }
    if (-not $match.Success) {
        if ($item.Name -in @('.release-work', 'cache')) {
            $targets.Add($item)
        }
        continue
    }

    $isCurrentVersion = $match.Groups['version'].Value -eq $KeepVersion
    $isUnpackedDirectory = $item.PSIsContainer -and $item.Name -like 'DeepSeekHarness-*'
    if (-not $isCurrentVersion -or ($isUnpackedDirectory -and -not $KeepUnpacked)) {
        $targets.Add($item)
    }
}

$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$removedBytes = 0L
foreach ($target in $targets) {
    $resolved = [IO.Path]::GetFullPath($target.FullName)
    if (-not $resolved.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($resolved) -ne $artifactRoot) {
        throw "Refusing to remove a path outside the artifacts root: $resolved"
    }

    if ($target.PSIsContainer) {
        $size = (Get-ChildItem -LiteralPath $resolved -File -Recurse -Force -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
        if ($null -ne $size) {
            $removedBytes += [long]$size
        }
    }
    else {
        $removedBytes += $target.Length
    }

    Write-Host "Removing $($target.Name)"
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$remainingBytes = (Get-ChildItem -LiteralPath $artifactRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum
if ($null -eq $remainingBytes) {
    $remainingBytes = 0
}

Write-Host ("Removed {0} item(s), freed {1:N2} MiB; artifacts now use {2:N2} MiB." -f
    $targets.Count,
    ($removedBytes / 1MB),
    ($remainingBytes / 1MB))
