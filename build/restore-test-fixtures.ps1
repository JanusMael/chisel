#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Prepare the test fixtures so `dotnet test` passes from a clean clone.

.DESCRIPTION
  The xUnit suite loads the example solutions under tests/Fixtures/ via MSBuildWorkspace
  at run time. Those solutions are NOT part of Chisel.slnx, so a normal `dotnet restore`
  of the repo never touches them — and MSBuildWorkspace does not restore on load. Without
  their project.assets.json the fixture compilations are missing references and the
  package/build tests fail.

  Additionally, the SourceGen fixture's `Gen` project is a source generator that `App`
  references as an analyzer; the SourceGenTests only discover the generated symbol once
  `Gen.dll` has been built. Restore alone does not produce it, so this script builds that
  one fixture solution.

  Run this once after cloning (or after deleting fixture bin/obj), then `dotnet test`.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot     = Resolve-Path (Join-Path $PSScriptRoot '..')
$fixturesRoot = Join-Path $repoRoot 'tests/Fixtures'

Get-ChildItem -Path $fixturesRoot -Recurse -Filter *.sln | ForEach-Object {
    Write-Host "==> restore $($_.Name)"
    dotnet restore $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "restore failed: $($_.FullName)" }
}

$sourceGen = Join-Path $fixturesRoot 'SourceGen/SourceGen.sln'
Write-Host "==> build SourceGen fixture (its generator assembly must exist for the tests)"
dotnet build $sourceGen -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed: $sourceGen" }

Write-Host "`nFixtures ready — run: dotnet test"
