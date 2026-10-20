<#
.SYNOPSIS
  Exports Brawler for Windows, Linux and macOS and zips each build into dist/, ready to attach to a GitHub release.

.DESCRIPTION
  Runs on Windows PowerShell 5.1 and on PowerShell 7 (pwsh), so the GitHub release workflow uses this same script.
  Each zip holds the game plus README.txt, the player guide (docs/INSTALL.md + docs/PLAYING_ONLINE.md).

  Needs the Godot .NET (mono) editor and its export templates for the same version. Pass -InstallTemplates to
  download the templates (about 1 GB, from the official Godot GitHub release) if they are missing.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/package.ps1 -Godot "C:\path\to\Godot_v4.7.2-stable_mono_win64_console.exe"

.EXAMPLE
  ./tools/package.ps1 -Godot $env:GODOT -Version 0.2.0 -Platforms windows,linux
#>
param(
    [string]$Godot = $env:GODOT,
    [string[]]$Platforms = @('windows', 'linux', 'macos'),
    [string]$Version = '',
    [switch]$InstallTemplates
)

$ErrorActionPreference = 'Stop'
# "-Platforms linux,macos" arrives as one string when the script is run with -File
$Platforms = @($Platforms | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'godot'
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
$projectFile = Join-Path $project 'project.godot'

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# Runs Godot and returns its output as plain text lines. Godot logs to stderr, which Windows PowerShell 5.1 would
# otherwise turn into terminating errors; colour codes and progress lines are dropped.
function Invoke-Godot {
    $ErrorActionPreference = 'Continue'
    & $Godot @args 2>&1 | ForEach-Object { "$_" -replace '\x1b\[[0-9;]*m', '' } | Where-Object { $_ -notmatch '^\s*\[\s*\d+% \]' }
}

if (-not $Godot) { Fail 'Pass -Godot <path to the Godot .NET editor> or set the GODOT environment variable.' }
if (-not (Test-Path $Godot)) { Fail "Godot not found at $Godot" }

# ---------- version ----------
$cfg = Get-Content $projectFile -Raw
if ($Version) {
    $Version = $Version.TrimStart('v')
    $cfg = $cfg -replace 'config/version="[^"]*"', "config/version=`"$Version`""
    [System.IO.File]::WriteAllText($projectFile, $cfg)
}
$m = [regex]::Match($cfg, 'config/version="([^"]*)"')
if (-not $m.Success) { Fail 'project.godot has no application/config/version.' }
$Version = $m.Groups[1].Value
Write-Host "Packaging Brawler $Version"

# ---------- export templates ----------
# "4.7.2.stable.mono.official.abc123" -> "4.7.2.stable.mono" (the templates folder name)
$godotVersion = (Invoke-Godot --headless --version | Select-Object -Last 1).Trim()
$parts = $godotVersion.Split('.')
$tplName = ($parts | Where-Object { $_ -ne 'official' -and $_ -notmatch '^[0-9a-f]{7,}$' }) -join '.'
if ($tplName -notmatch 'mono') { Fail "$Godot is not the .NET build of Godot (version $godotVersion). Use the 'mono' download." }

if ($IsLinux) { $tplRoot = Join-Path $HOME '.local/share/godot/export_templates' }
elseif ($IsMacOS) { $tplRoot = Join-Path $HOME 'Library/Application Support/Godot/export_templates' }
else { $tplRoot = Join-Path $env:APPDATA 'Godot\export_templates' }
$tplDir = Join-Path $tplRoot $tplName

if (-not (Test-Path (Join-Path $tplDir 'version.txt'))) {
    if (-not $InstallTemplates) { Fail "Export templates for $tplName are missing ($tplDir). Re-run with -InstallTemplates, or install them in the editor: Editor > Manage Export Templates." }
    # 4.7.2.stable.mono -> https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_export_templates.tpz
    $num = ($tplName -split '\.stable')[0]
    $url = "https://github.com/godotengine/godot/releases/download/$num-stable/Godot_v$num-stable_mono_export_templates.tpz"
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "brawler-templates-$num"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp 'templates.zip'
    Write-Host "Downloading export templates from $url"
    $ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower
    Invoke-WebRequest -Uri $url -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force $tplDir | Out-Null
    Copy-Item -Path (Join-Path $tmp 'templates/*') -Destination $tplDir -Recurse -Force
    Remove-Item -Recurse -Force $tmp
}

# ---------- export ----------
$presets = @{ windows = 'Windows'; linux = 'Linux'; macos = 'macOS' }
$outputs = @{ windows = 'Brawler.exe'; linux = 'Brawler.x86_64'; macos = 'Brawler.zip' }

$guide = Join-Path $build 'README.txt'
New-Item -ItemType Directory -Force $build, $dist | Out-Null
$text = (Get-Content (Join-Path $root 'docs/INSTALL.md') -Raw) + "`n`n" + (Get-Content (Join-Path $root 'docs/PLAYING_ONLINE.md') -Raw)
[System.IO.File]::WriteAllText($guide, $text.Replace('{VERSION}', $Version))

Write-Host 'Importing assets...'
Invoke-Godot --headless --path $project --import | Out-Null

$haveZip = [bool](Get-Command zip -ErrorAction SilentlyContinue) # Info-ZIP keeps the Linux executable bit

# Fallback zipper for Windows. Compress-Archive in PowerShell 5.1 writes backslash paths, which unzip on Linux and
# macOS treats as part of the file name, so entries are added one by one with forward slashes.
function Add-ToZip($zipPath, $baseDir, $files) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $base = (Resolve-Path $baseDir).Path.TrimEnd('\', '/')
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, 'Update')
    try {
        foreach ($f in $files) {
            $name = $f.Substring($base.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f, $name, 'Optimal') | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
$made = @()
foreach ($p in $Platforms) {
    $p = $p.ToLower()
    if (-not $presets.ContainsKey($p)) { Fail "Unknown platform '$p' (use windows, linux, macos)." }
    $dir = Join-Path $build $p
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir $outputs[$p]

    Write-Host "Exporting $($presets[$p])..."
    Invoke-Godot --headless --path $project --export-release $presets[$p] $out | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $out)) { Fail "Export for $p failed (exit $LASTEXITCODE). See the log above." }

    $zipPath = Join-Path $dist "Brawler-$Version-$p.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    if ($p -eq 'macos') {
        # Godot already zips the .app bundle; add the guide next to it.
        Copy-Item $out $zipPath
        if ($haveZip) { & zip -qj $zipPath $guide }
        else { Add-ToZip $zipPath $build @($guide) }
    }
    else {
        Copy-Item $guide $dir
        if ($haveZip) { Push-Location $dir; & zip -qr $zipPath .; Pop-Location }
        else { Add-ToZip $zipPath $dir @(Get-ChildItem $dir -Recurse -File | ForEach-Object { $_.FullName }) }
    }
    $made += $zipPath
}

Write-Host ''
Write-Host 'Done:' -ForegroundColor Green
foreach ($z in $made) { Write-Host ("  {0}  ({1:N1} MB)" -f $z, ((Get-Item $z).Length / 1MB)) }
