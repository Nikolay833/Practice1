<#
.SYNOPSIS
  Builds an unsigned Dropshelf.msixbundle that holds an x64 and an arm64 package (Windows installs the matching one).

.DESCRIPTION
  No Visual Studio packaging project: for each architecture the app is published self-contained (not single-file) into a
  layout folder, packaging/Package.appxmanifest and packaging/Assets are added, and makeappx.exe from the Windows SDK packs
  the folder. makeappx bundle then joins the packages. Sign the result with sign-msix.ps1.

.PARAMETER Version
  Four-part package version, e.g. 1.0.42.0. It must grow with every release or Windows refuses to update the package.

.PARAMETER OutDir
  Where Dropshelf.msixbundle is written. Intermediate files go to <OutDir>\work.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutDir = 'msix-out',
    [string[]]$Architectures = @('x64', 'arm64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sdk-tools.ps1')

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\Dropshelf\Dropshelf.csproj'
$template = Join-Path $PSScriptRoot 'Package.appxmanifest'
$assets = Join-Path $PSScriptRoot 'Assets'
$out = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path (Get-Location).Path $OutDir }
$work = Join-Path $out 'work'
$assemblyVersion = ($Version.Split('.')[0..2]) -join '.'

Write-Host "Version $Version, output $out"
$makeappx = Find-SdkTool 'makeappx.exe'

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$packages = Join-Path $work 'packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null

foreach ($arch in $Architectures) {
    Write-Host "::group::Package $arch"
    $layout = Join-Path $work "layout-$arch"

    # Same self-contained publish as the portable exes, but loose files: an MSIX is its own installer.
    Invoke-Native 'dotnet' @(
        'publish', $project, '-c', 'Release', '-r', "win-$arch", '--self-contained', 'true',
        "-p:Version=$assemblyVersion", '-p:PublishSingleFile=false', '-o', $layout)

    if (-not (Test-Path -LiteralPath (Join-Path $layout 'Dropshelf.exe'))) {
        throw "Dropshelf.exe is missing from $layout"
    }

    Get-ChildItem -LiteralPath $layout -Recurse -Filter '*.pdb' | Remove-Item -Force

    $manifest = (Get-Content -LiteralPath $template -Raw).Replace('{{VERSION}}', $Version).Replace('{{ARCH}}', $arch)
    if ($manifest -match '\{\{') { throw 'Package.appxmanifest still has an unreplaced {{token}}.' }
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    $layoutAssets = Join-Path $layout 'Assets'
    New-Item -ItemType Directory -Force -Path $layoutAssets | Out-Null
    Copy-Item -Path (Join-Path $assets '*') -Destination $layoutAssets -Force

    $package = Join-Path $packages "Dropshelf_${Version}_$arch.msix"
    Invoke-Native $makeappx @('pack', '/d', $layout, '/p', $package, '/o')
    Write-Host "::endgroup::"
}

Write-Host '::group::Bundle'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$bundle = Join-Path $out 'Dropshelf.msixbundle'
Invoke-Native $makeappx @('bundle', '/d', $packages, '/bv', $Version, '/p', $bundle, '/o')
Write-Host "::endgroup::"

Write-Host ("Built {0} ({1:N1} MB)" -f $bundle, ((Get-Item -LiteralPath $bundle).Length / 1MB))
