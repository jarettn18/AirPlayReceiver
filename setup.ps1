# One-time setup: installs MSYS2 + GStreamer, builds UxPlay, and opens the firewall.
# Safe to re-run; finished steps are skipped (UxPlay is updated and rebuilt).
$ErrorActionPreference = 'Stop'

$Root     = $PSScriptRoot
$Msys     = 'C:\msys64'
$Bash     = Join-Path $Msys 'usr\bin\bash.exe'
$SrcDir   = Join-Path $Root 'UxPlay'
$Exe      = Join-Path $SrcDir 'build\uxplay.exe'
$RuleName = 'UxPlay AirPlay Receiver'

function Invoke-Ucrt64([string]$cmd) {
    $env:MSYSTEM = 'UCRT64'
    $env:CHERE_INVOKING = '1'
    & $Bash -lc $cmd
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed ($LASTEXITCODE): $cmd" }
}

# 1. MSYS2
if (-not (Test-Path $Bash)) {
    Write-Host '==> Installing MSYS2...' -ForegroundColor Cyan
    winget install --id MSYS2.MSYS2 -e --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $Bash)) { throw "MSYS2 not found at $Msys after install." }
} else {
    Write-Host '==> MSYS2 already installed.' -ForegroundColor Green
}

# 2. Toolchain + GStreamer (first -Syu may only update the core and exit, so run it twice)
Write-Host '==> Updating MSYS2 and installing build tools + GStreamer...' -ForegroundColor Cyan
Invoke-Ucrt64 'pacman -Syu --noconfirm'
Invoke-Ucrt64 'pacman -Syu --noconfirm'
$pkgs = 'cmake gcc ninja libplist gstreamer gst-plugins-base gst-plugins-good gst-plugins-bad gst-libav' -split ' ' |
    ForEach-Object { "mingw-w64-ucrt-x86_64-$_" }
Invoke-Ucrt64 ("pacman -S --needed --noconfirm git " + ($pkgs -join ' '))

# 3. Get UxPlay source
if (-not (Test-Path (Join-Path $SrcDir '.git'))) {
    Write-Host '==> Cloning UxPlay...' -ForegroundColor Cyan
    git clone --depth 1 https://github.com/FDH2/UxPlay $SrcDir
} else {
    Write-Host '==> Updating UxPlay source...' -ForegroundColor Cyan
    git -C $SrcDir pull --ff-only
}
if ($LASTEXITCODE -ne 0) { throw 'git failed.' }

# 4. Build
Write-Host '==> Building UxPlay...' -ForegroundColor Cyan
$srcUnix = (& $Bash -lc "cygpath -u '$SrcDir'").Trim()
Invoke-Ucrt64 "cd '$srcUnix' && mkdir -p build && cd build && cmake -G Ninja .. && ninja"
if (-not (Test-Path $Exe)) { throw "Build finished but $Exe was not found." }
Write-Host "==> Built $Exe" -ForegroundColor Green

# 5. Firewall (needs admin; prompts for elevation once)
if (-not (Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)) {
    Write-Host '==> Adding firewall rule (approve the admin prompt)...' -ForegroundColor Cyan
    $fw = "New-NetFirewallRule -DisplayName '$RuleName' -Direction Inbound -Program '$Exe' -Action Allow -Profile Private -Protocol Any | Out-Null"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $fw
} else {
    Write-Host '==> Firewall rule already present.' -ForegroundColor Green
}

Write-Host "`nSetup complete. Double-click start-receiver.bat to start mirroring." -ForegroundColor Green
