# Bundles uxplay.exe, its MSYS2 DLLs and all GStreamer plugins into one portable
# dist\AirPlayReceiver.exe. Run setup.ps1 first so UxPlay is built.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem, System.Drawing

$Root     = Split-Path $PSScriptRoot -Parent
$Ucrt     = 'C:\msys64\ucrt64'
$UcrtBin  = Join-Path $Ucrt 'bin'
$PluginSrc = Join-Path $Ucrt 'lib\gstreamer-1.0'
$Scanner  = Join-Path $Ucrt 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
$Objdump  = Join-Path $UcrtBin 'objdump.exe'
$Exe      = Join-Path $Root 'UxPlay\build\uxplay.exe'
$Dist     = Join-Path $Root 'dist'
$Stage    = Join-Path $Dist 'stage'
$Zip      = Join-Path $Dist 'runtime.zip'
$Csc      = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$Sys32    = Join-Path $env:WINDIR 'System32'

if (-not (Test-Path $Exe)) { throw "uxplay.exe not found. Run setup.ps1 first." }

# --- DLL dependency resolution (import tables via objdump) ---
$importCache = @{}
function Get-Imports([string]$file) {
    if (-not $importCache.ContainsKey($file)) {
        $importCache[$file] = @(& $Objdump -p $file | Select-String '^\s*DLL Name:\s*(\S+)' |
            ForEach-Object { $_.Matches[0].Groups[1].Value })
    }
    $importCache[$file]
}

# Returns @{ Dlls = full paths under ucrt64\bin; Missing = unresolvable names }
function Get-Closure([string[]]$roots) {
    $dlls = @{}; $missing = @{}
    $queue = New-Object System.Collections.Queue
    $roots | ForEach-Object { $queue.Enqueue($_) }
    while ($queue.Count) {
        foreach ($name in Get-Imports $queue.Dequeue()) {
            $key = $name.ToLower()
            if ($dlls.ContainsKey($key) -or $missing.ContainsKey($key)) { continue }
            $local = Join-Path $UcrtBin $name
            if (Test-Path $local) { $dlls[$key] = $local; $queue.Enqueue($local) }
            elseif ($key -like 'api-ms-*' -or $key -like 'ext-ms-*' -or (Test-Path (Join-Path $Sys32 $name))) { }
            else { $missing[$key] = $name }
        }
    }
    @{ Dlls = $dlls; Missing = @($missing.Values) }
}

# --- 1. Stage ---
Write-Host '==> Staging runtime...' -ForegroundColor Cyan
if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
$binDir     = New-Item -ItemType Directory (Join-Path $Stage 'bin')
$pluginDir  = New-Item -ItemType Directory (Join-Path $Stage 'lib\gstreamer-1.0')
$libexecDir = New-Item -ItemType Directory (Join-Path $Stage 'libexec\gstreamer-1.0')

$allDlls = @{}
$base = Get-Closure @($Exe, $Scanner)
if ($base.Missing.Count) { throw "uxplay.exe has unresolved dependencies: $($base.Missing -join ', ')" }
$base.Dlls.GetEnumerator() | ForEach-Object { $allDlls[$_.Key] = $_.Value }

# --- 2. Plugins: keep every plugin whose dependencies resolve ---
$plugins = Get-ChildItem $PluginSrc -Filter *.dll
$skipped = @()
$i = 0
foreach ($p in $plugins) {
    $i++
    Write-Progress -Activity 'Resolving plugin dependencies' -Status $p.Name -PercentComplete (100 * $i / $plugins.Count)
    $c = Get-Closure @($p.FullName)
    if ($c.Missing.Count) { $skipped += "$($p.Name) (missing $($c.Missing -join ', '))"; continue }
    Copy-Item $p.FullName $pluginDir
    $c.Dlls.GetEnumerator() | ForEach-Object { $allDlls[$_.Key] = $_.Value }
}
Write-Progress -Activity 'Resolving plugin dependencies' -Completed
if ($skipped) {
    Write-Warning "Skipped $($skipped.Count) plugin(s) with unresolved dependencies:"
    $skipped | ForEach-Object { Write-Warning "  $_" }
}

Copy-Item $Exe $binDir
Copy-Item $Scanner $libexecDir
$allDlls.Values | ForEach-Object { Copy-Item $_ $binDir }
Write-Host ("    {0} plugins, {1} DLLs" -f ($plugins.Count - $skipped.Count), $allDlls.Count)

# --- 3. Licences ---
Copy-Item (Join-Path $Root 'UxPlay\LICENSE') (Join-Path $Stage 'LICENSE-UxPlay.txt')
@'
This package bundles third-party software:

- UxPlay (GPLv3) - https://github.com/FDH2/UxPlay
- GStreamer and its plugins (LGPLv2.1+) - https://gstreamer.freedesktop.org
- FFmpeg via gst-libav (LGPL/GPL) - https://ffmpeg.org
- Other libraries from the MSYS2 UCRT64 repository, each under its own licence.
  Sources: https://github.com/msys2/MINGW-packages
'@ | Set-Content (Join-Path $Stage 'LICENSES.txt') -Encoding utf8

# --- 4. Zip + hash ---
Write-Host '==> Compressing runtime...' -ForegroundColor Cyan
[System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip, 'Optimal', $false)
$hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.Substring(0, 16)

# --- 5. Compile launcher ---
Write-Host '==> Compiling launcher...' -ForegroundColor Cyan
$buildInfo = Join-Path $Dist 'BuildInfo.cs'
"static class BuildInfo { public const string RuntimeHash = `"$hash`"; }" | Set-Content $buildInfo -Encoding utf8

$ico = Join-Path $Dist 'app.ico'
$fs = [System.IO.File]::Create($ico)
try { [System.Drawing.Icon]::ExtractAssociatedIcon($Exe).Save($fs) } finally { $fs.Close() }

$out = Join-Path $Dist 'AirPlayReceiver.exe'
& $Csc /nologo /optimize /target:winexe "/out:$out" "/win32icon:$ico" `
    "/resource:$Zip,runtime.zip" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    (Join-Path $PSScriptRoot 'AirPlayReceiver.cs') $buildInfo
if ($LASTEXITCODE -ne 0) { throw 'csc failed.' }

# --- 6. Clean up intermediates ---
Remove-Item $Stage, $Zip, $buildInfo, $ico -Recurse -Force
Write-Host ("==> Built {0} ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB)) -ForegroundColor Green
