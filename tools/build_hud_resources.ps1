<#
.SYNOPSIS
    Validates, compiles and packs the CSRoll custom HUD Panorama resources.

.DESCRIPTION
    The HUD is a SEPARATE DELIVERABLE from the plugin. `dotnet publish` produces CSRoll.zip, which
    servers install; this script produces a Workshop addon, which PLAYERS download. Neither contains
    the other, and both are required for the HUD to appear on screen.

    That split is not a design choice - CS2's custom_hud_layout resolves its layout path on the
    client, so the .vxml_c / .vcss_c have to already be in the player's game files. There is no
    mechanism for a server plugin to deliver them.

    Only -Action Validate runs on macOS/Linux. Compile, Pack and Install need resourcecompiler.exe,
    which ships with the Windows-only CS2 Workshop Tools.

.PARAMETER Action
    Validate  Check the id/class contract and the panel-type allowlist. No compile. Runs anywhere.
    Compile   Stage the sources into the addon content tree and run resourcecompiler.
    Pack      Build the .vpk from the compiled addon.
    Build     Validate, then Compile, then Pack.
    Install   Copy the .vpk into a local CS2 install and print the gameinfo.gi lines to add.
              Local development only - production delivery is via the Steam Workshop.

.PARAMETER CS2Root
    The CS2 install root, i.e. the folder containing game\ and content\.
    Typically "...\steamapps\common\Counter-Strike Global Offensive".

.PARAMETER VpkEditCli
    Path to vpkeditcli.exe (https://github.com/craftablescience/VPKEdit). Required for Pack.

.PARAMETER AddonName
    Workshop addon folder name. Must match on both the content and game side.

.EXAMPLE
    pwsh tools/build_hud_resources.ps1 -Action Validate

.EXAMPLE
    pwsh tools\build_hud_resources.ps1 -Action Build `
        -CS2Root "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" `
        -VpkEditCli "C:\Tools\VPKEdit\vpkeditcli.exe"
#>

[CmdletBinding()]
param(
    [ValidateSet('Validate', 'Compile', 'Pack', 'Build', 'Install')]
    [string] $Action = 'Validate',

    [string] $CS2Root,
    [string] $VpkEditCli,
    [string] $AddonName = 'csroll_hud',
    [string] $OutputDir = 'build/hud'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$HudSource = Join-Path $RepoRoot 'hud'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string] $Message) { Write-Host "    $Message" -ForegroundColor Green }

function Assert-Windows([string] $Action) {
    if ($IsWindows -eq $false) {
        Write-Error @"
-Action $Action needs resourcecompiler.exe, which is part of the Windows-only CS2 Workshop Tools.

Author the layout, stylesheet and presentation data on this machine and check that they agree with:

    python3 tools/validate_hud_contract.py

then run the compile and publish steps from a Windows box that has CS2 + Workshop Tools installed.
See tools/HUD_SETUP.md.
"@
        exit 2
    }
}

function Resolve-CS2Root {
    if (-not $CS2Root) {
        Write-Error "-CS2Root is required for -Action $Action. See tools/HUD_SETUP.md."
        exit 2
    }
    if (-not (Test-Path $CS2Root)) {
        Write-Error "CS2Root '$CS2Root' does not exist."
        exit 2
    }
    return (Resolve-Path $CS2Root).Path
}

# -------------------------------------------------------------------------------------------------
# Validate
#
# Delegated to a Python script rather than reimplemented here, for one reason: it has to run on the
# machine the HUD is authored on, and that is not necessarily Windows. It is also the only check that
# can catch a wrong panel id at all - CS2 gives no runtime signal for one.
# -------------------------------------------------------------------------------------------------
function Invoke-Validate {
    Write-Step 'Validating the HUD contract (ids, classes, panel types, presentation data)'

    $validator = Join-Path $PSScriptRoot 'validate_hud_contract.py'
    if (-not (Test-Path $validator)) {
        Write-Error "Validator not found at $validator"
        exit 1
    }

    $python = $null
    foreach ($candidate in @('python3', 'python', 'py')) {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) { $python = $candidate; break }
    }

    if (-not $python) {
        Write-Error @"
Python 3 was not found on PATH, so the HUD contract cannot be validated.

This is worth installing rather than skipping. A wrong panel id produces no exception, no log line,
and no visual difference from an empty value - this check is the only thing that catches one before
a player does.
"@
        exit 1
    }

    & $python $validator --repo-root $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'HUD contract validation failed. Fix the errors above before compiling.'
        exit 1
    }

    Write-Ok 'Contract OK.'
}

# -------------------------------------------------------------------------------------------------
# Compile
# -------------------------------------------------------------------------------------------------
function Invoke-Compile {
    Assert-Windows 'Compile'
    $root = Resolve-CS2Root

    $compiler = Join-Path $root 'game\bin\win64\resourcecompiler.exe'
    if (-not (Test-Path $compiler)) {
        Write-Error @"
resourcecompiler.exe not found at:
    $compiler

Install "Counter-Strike 2 Workshop Tools" from Steam (Library -> Tools) and try again.
"@
        exit 2
    }

    $contentRoot = Join-Path $root "content\csgo_addons\$AddonName"
    $layoutDir = Join-Path $contentRoot 'panorama\layout\custom_game'
    $stylesDir = Join-Path $contentRoot 'panorama\styles\custom_game'
    $imagesDir = Join-Path $contentRoot 'panorama\images\custom_game\csroll'

    Write-Step "Staging sources into $contentRoot"
    foreach ($dir in @($layoutDir, $stylesDir, $imagesDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    Copy-Item (Join-Path $HudSource 'layout\*.xml') $layoutDir -Force
    Copy-Item (Join-Path $HudSource 'styles\*.css') $stylesDir -Force

    $images = Join-Path $HudSource 'images'
    if (Test-Path $images) {
        $pngs = Get-ChildItem -Path $images -Filter '*.png' -File -ErrorAction SilentlyContinue
        if ($pngs) { Copy-Item $pngs.FullName $imagesDir -Force }
    }

    Write-Ok 'Staged.'

    Write-Step 'Running resourcecompiler'
    & $compiler -r -f -i (Join-Path $contentRoot 'panorama\*')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "resourcecompiler failed with exit code $LASTEXITCODE."
        exit 1
    }

    $gameRoot = Join-Path $root "game\csgo_addons\$AddonName"
    $compiled = @(Get-ChildItem -Path $gameRoot -Recurse -Include '*.vxml_c', '*.vcss_c' -ErrorAction SilentlyContinue)
    if ($compiled.Count -eq 0) {
        Write-Error @"
resourcecompiler reported success but produced no .vxml_c / .vcss_c under:
    $gameRoot

This is almost always the AddonConfig -> VpkDirectories problem described in tools/HUD_SETUP.md:
without panorama/layout/custom_game and panorama/styles/custom_game listed there, the panorama files
are silently skipped and everything downstream still 'succeeds'.
"@
        exit 1
    }

    Write-Ok "Compiled $($compiled.Count) resource(s) into $gameRoot"
}

# -------------------------------------------------------------------------------------------------
# Pack
# -------------------------------------------------------------------------------------------------
function Invoke-Pack {
    Assert-Windows 'Pack'
    $root = Resolve-CS2Root

    if (-not $VpkEditCli) {
        Write-Error '-VpkEditCli is required for -Action Pack. Get it from https://github.com/craftablescience/VPKEdit'
        exit 2
    }
    if (-not (Test-Path $VpkEditCli)) {
        Write-Error "vpkeditcli.exe not found at '$VpkEditCli'."
        exit 2
    }

    $gameRoot = Join-Path $root "game\csgo_addons\$AddonName"
    if (-not (Test-Path $gameRoot)) {
        Write-Error "Nothing compiled at $gameRoot - run -Action Compile first."
        exit 1
    }

    $outDir = Join-Path $RepoRoot $OutputDir
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $vpk = Join-Path $outDir "$AddonName.vpk"

    Write-Step "Packing $gameRoot -> $vpk"
    & $VpkEditCli --output $vpk $gameRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Error "vpkeditcli failed with exit code $LASTEXITCODE."
        exit 1
    }

    Write-Ok "Wrote $vpk"
}

# -------------------------------------------------------------------------------------------------
# Install (local development only)
# -------------------------------------------------------------------------------------------------
function Invoke-Install {
    Assert-Windows 'Install'
    $root = Resolve-CS2Root

    $vpk = Join-Path (Join-Path $RepoRoot $OutputDir) "$AddonName.vpk"
    if (-not (Test-Path $vpk)) {
        Write-Error "No VPK at $vpk - run -Action Build first."
        exit 1
    }

    $overrides = Join-Path $root 'game\csgo\overrides'
    New-Item -ItemType Directory -Force -Path $overrides | Out-Null
    Copy-Item $vpk $overrides -Force

    Write-Ok "Copied to $overrides"
    Write-Host @"

    This is a LOCAL DEVELOPMENT shortcut, not how players get the HUD. To finish the local mount, add
    this line to $root\game\csgo\gameinfo.gi, inside FileSystem -> SearchPaths, ABOVE the existing
    "Game    csgo" entry:

        Game    csgo/overrides/$AddonName.vpk

    Back the file up first - a CS2 update can overwrite it. Restart CS2 afterwards.

    For production, publish the addon to the Steam Workshop and deliver it with AddonsManager -
    see tools/HUD_SETUP.md.
"@ -ForegroundColor Yellow
}

# -------------------------------------------------------------------------------------------------

switch ($Action) {
    'Validate' { Invoke-Validate }
    'Compile' { Invoke-Compile }
    'Pack' { Invoke-Pack }
    'Build' { Invoke-Validate; Invoke-Compile; Invoke-Pack }
    'Install' { Invoke-Install }
}

Write-Host ''
Write-Ok "Action '$Action' completed."
