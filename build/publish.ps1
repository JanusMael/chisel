#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Publish self-contained chisel binaries per runtime identifier and archive each into
  ./dist (.zip for win-* RIDs, .tar.gz otherwise).

.DESCRIPTION
  The Release workflow runs this on each native host (so osx/win/linux RIDs build on
  their own architecture); developers can run it locally for identical artifacts.

  CAVEAT — these binaries are a convenience download, not the recommended install.
  chisel locates MSBuild from an *installed .NET SDK* at run time (via MSBuildLocator),
  and the Microsoft.Build.* assemblies are referenced with ExcludeAssets=runtime, so they
  are deliberately NOT bundled. A self-contained binary therefore still requires a .NET
  SDK on the target machine. The recommended install path is:
      dotnet tool install --global Bennewitz.Ninja.Chisel

.EXAMPLE
  ./build/publish.ps1 -Rids win-x64,win-arm64 -Version 1.2.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Rids,
    [string] $Version = '0.0.0-dev',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project  = Join-Path $repoRoot 'src/Chisel.Cli/Chisel.Cli.csproj'
$distDir  = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

foreach ($ridRaw in $Rids) {
    $rid = $ridRaw.Trim()
    if (-not $rid) { continue }

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("chisel-$rid-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    Write-Host "==> publish $rid"
    # Folder publish (no single-file / no trim): single-file & trim analyzers would flag
    # MSBuildLocator/Roslyn reflection (IL2xxx/IL3xxx) and, under TreatWarningsAsErrors,
    # fail the publish. A plain self-contained folder sidesteps that.
    dotnet publish $project -c $Configuration -r $rid --self-contained true -p:Version=$Version -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid ($LASTEXITCODE)" }

    $archiveBase = "chisel-$Version-$rid"
    if ($rid -like 'win-*') {
        $archive = Join-Path $distDir "$archiveBase.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
    }
    else {
        $archive = Join-Path $distDir "$archiveBase.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        # tar ships on the ubuntu/macos runners; -C stages the cwd so paths aren't absolute.
        tar -czf $archive -C $stage .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid ($LASTEXITCODE)" }
    }
    Write-Host "==> $archive"

    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`nArtifacts in $distDir :"
Get-ChildItem $distDir | ForEach-Object { Write-Host "  $($_.Name)" }
