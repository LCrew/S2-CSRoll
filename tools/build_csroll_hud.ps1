<#
.SYNOPSIS
    Builds the CSRoll HUD Workshop addon from hud\panorama.

.DESCRIPTION
    1. Copies hud\panorama into <CS2>\content\csgo_addons\<Addon>\panorama - the addon's source tree.
    2. Compiles every .xml, .css and .svg there with resourcecompiler.exe, which ships with the
       Counter-Strike 2 Workshop Tools. PNGs have no compiler of their own: they are built as child
       resources of the stylesheet, which references them.
    3. Checks that every compiled file (.vxml_c, .vcss_c, .vsvg_c, _png.vtex_c) landed in
       <CS2>\game\csgo_addons\<Addon>\panorama. resourcecompiler can report success and write nothing,
       and an addon published without them looks fine everywhere and draws nothing in game.

    Publishing is a separate, manual step in the Workshop Manager - see hud\README.md.

    -Deploy also copies the compiled files into game\csgo\overrides\panorama for a local test on your
    own machine. That needs a directory search path in gameinfo.gi and marks your client as modified:
    VAC-secured servers will refuse your connection until you undo it.

.EXAMPLE
    .\tools\build_csroll_hud.ps1

.EXAMPLE
    .\tools\build_csroll_hud.ps1 -Cs2Root "D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"

.NOTES
    Written without a Windows machine to run it on, against the same resourcecompiler flags as the
    cs2-panorama-hud skill's build script - so the first run is the real test. Every step prints what
    it is doing and stops at the first failure.
#>
[CmdletBinding()]
param(
    [string] $Cs2Root = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive',
    [string] $Addon   = 'csroll_hud',
    [switch] $Deploy,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$repo         = Resolve-Path (Join-Path $PSScriptRoot '..')
$source       = Join-Path $repo 'hud\panorama'
$compiler     = Join-Path $Cs2Root 'game\bin\win64\resourcecompiler.exe'
$addonContent = Join-Path $Cs2Root "content\csgo_addons\$Addon"
$contentDir   = Join-Path $addonContent 'panorama'
$gameDir      = Join-Path $Cs2Root "game\csgo_addons\$Addon\panorama"
$overrides    = Join-Path $Cs2Root 'game\csgo\overrides\panorama'

if (-not (Test-Path $source)) {
    throw "hud\panorama not found under $repo - run this from the CSRoll repository."
}
if (-not (Test-Path $compiler)) {
    throw "resourcecompiler.exe not found at $compiler`nInstall 'Counter-Strike 2 Workshop Tools' (Steam > Library > Tools), or pass -Cs2Root if CS2 lives elsewhere."
}
if (-not (Test-Path $addonContent)) {
    throw "Addon '$Addon' not found at $addonContent`nCreate it first: launch Counter-Strike 2 Workshop Tools > Create New Addon > name it '$Addon'."
}

Write-Host "`n[1/3] Copying hud\panorama -> $contentDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $contentDir | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $contentDir -Recurse -Force

# Images first, the stylesheet that references them last.
$sources = @(Get-ChildItem -Path $contentDir -Recurse -File -Include *.svg) +
           @(Get-ChildItem -Path $contentDir -Recurse -File -Include *.xml, *.css)
if (-not $sources) { throw "Nothing to compile under $contentDir" }
$images  = @(Get-ChildItem -Path $contentDir -Recurse -File -Include *.png)

Write-Host "`n[2/3] Compiling $($sources.Count) file(s)" -ForegroundColor Cyan
foreach ($src in $sources) {
    # resourcecompiler maps content\ to game\ by convention, so it only needs the input path.
    $rcArgs = @('-i', $src.FullName)
    if ($Force) { $rcArgs += '-f' }

    $output = & $compiler @rcArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host "    $_" }
        throw "resourcecompiler failed on $($src.Name) (exit $LASTEXITCODE)"
    }
    Write-Host "    ok  $($src.FullName.Substring($contentDir.Length + 1))"
}

Write-Host "`n[3/3] Checking compiled output in $gameDir" -ForegroundColor Cyan
# Panorama images compile with the source extension folded into the name: icon.png -> icon_png.vtex_c.
$suffix  = @{ '.xml' = '.vxml_c'; '.css' = '.vcss_c'; '.svg' = '.vsvg_c'; '.png' = '_png.vtex_c' }
$missing  = @()
$noPng    = @()
foreach ($src in ($sources + $images)) {
    $relative = $src.FullName.Substring($contentDir.Length + 1)
    $stem     = $relative.Substring(0, $relative.Length - $src.Extension.Length)
    $compiled = Join-Path $gameDir ($stem + $suffix[$src.Extension.ToLower()])
    if (-not (Test-Path $compiled)) {
        if ($src.Extension -eq '.png') { $noPng += $compiled } else { $missing += $compiled }
    }
}
if ($missing.Count -gt 0) {
    Write-Host "    Missing compiled files:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "      $_" }
    Write-Host "    What resourcecompiler did produce:" -ForegroundColor Yellow
    Get-ChildItem -Path $gameDir -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 20 | ForEach-Object { Write-Host "      $($_.FullName)" }
    throw "$($missing.Count) file(s) were not compiled - nothing to publish yet."
}
if ($noPng.Count -gt 0) {
    # Not fatal: the PNG icon layer is a fallback for the SVG one. Report what did come out instead.
    Write-Host "    WARNING: $($noPng.Count) PNG icon(s) not found as _png.vtex_c - only the SVG icon layer will work." -ForegroundColor Yellow
    Write-Host "    Image files resourcecompiler produced:" -ForegroundColor Yellow
    Get-ChildItem -Path (Join-Path $gameDir 'images') -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.vsvg_c' } | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.FullName)" }
}
Write-Host "    all $($sources.Count) required compiled files present"

if ($Deploy) {
    Write-Host "`n[+] Copying compiled files to $overrides (local test only)" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $overrides | Out-Null
    Copy-Item -Path (Join-Path $gameDir '*') -Destination $overrides -Recurse -Force
    Write-Host "    Needs this line in game\csgo\gameinfo.gi under FileSystem > SearchPaths, ABOVE 'Game csgo':"
    Write-Host "        Game    csgo/overrides" -ForegroundColor Yellow
    Write-Host "    Undo it afterwards - VAC-secured servers refuse modified clients."
}

Write-Host "`nDone. Publish with the Counter-Strike 2 Workshop Manager - see hud\README.md." -ForegroundColor Green
