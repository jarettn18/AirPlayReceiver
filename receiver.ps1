# Runs uxplay.exe without a console and adds a tray icon to stop it.
# Settings come from environment variables set by start-receiver.bat.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$Exe      = Join-Path $PSScriptRoot 'UxPlay\build\uxplay.exe'
$Name     = if ($env:RECEIVER_NAME) { $env:RECEIVER_NAME } else { 'AirPlay-PC' }
$Extra    = $env:EXTRA_ARGS
$ExitOnWindowClose = $env:EXIT_ON_WINDOW_CLOSE -ne '0'
$Log      = Join-Path $env:TEMP 'airplay-receiver.log'

function Show-Error([string]$msg) {
    [System.Windows.Forms.MessageBox]::Show($msg, 'AirPlay Receiver', 'OK', 'Error') | Out-Null
}

if (-not (Test-Path $Exe)) { Show-Error "uxplay.exe not found. Run setup.ps1 first."; exit 1 }
if (Get-Process uxplay -ErrorAction SilentlyContinue) {
    Show-Error "The receiver is already running. Use the tray icon or stop-receiver.bat to stop it."
    exit 1
}

$env:PATH = "C:\msys64\ucrt64\bin;$env:PATH"
$proc = Start-Process -FilePath $Exe -ArgumentList "-n `"$Name`" -nh $Extra" `
    -WindowStyle Hidden -RedirectStandardOutput $Log -RedirectStandardError "$Log.err" -PassThru
$started   = Get-Date
$stopping  = $false
$sawWindow = $false

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Application
try { $tray.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe) } catch {}
$tray.Text = "AirPlay Receiver: $Name"
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$status = $menu.Items.Add("$Name - waiting for iPhone")
$status.Enabled = $false
$menu.Items.Add('Open log', $null, { Start-Process notepad.exe $Log }) | Out-Null
$menu.Items.Add('Stop receiver', $null, { Stop-Receiver }) | Out-Null
$tray.ContextMenuStrip = $menu
$tray.Visible = $true

function Stop-Receiver {
    $script:stopping = $true
    if (-not $proc.HasExited) { $proc.Kill() }
}

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({
    if ($proc.HasExited) {
        $timer.Stop()
        $tray.Visible = $false
        # Exiting within a few seconds of launch means UxPlay failed to start.
        if (-not $stopping -and ((Get-Date) - $started).TotalSeconds -lt 5) {
            $tail = (Get-Content $Log, "$Log.err" -Tail 15 -ErrorAction SilentlyContinue) -join "`n"
            Show-Error "UxPlay exited right after starting:`n`n$tail"
        }
        [System.Windows.Forms.Application]::Exit()
        return
    }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $script:sawWindow = $true
        $status.Text = "$Name - mirroring"
    } elseif ($sawWindow) {
        $script:sawWindow = $false
        $status.Text = "$Name - waiting for iPhone"
        if ($ExitOnWindowClose) { Stop-Receiver }
    }
})
$timer.Start()

[System.Windows.Forms.Application]::Run()
$tray.Dispose()
