# AirPlay Receiver for Windows

Mirror your iPhone screen into a window on this PC. This uses [UxPlay](https://github.com/FDH2/UxPlay), an open-source AirPlay server.

## Setup (once)

In PowerShell in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

This installs MSYS2 with GStreamer, builds UxPlay into `UxPlay\build\`, and adds a firewall rule. The firewall step shows one admin prompt.

Run `setup.ps1` again only if you move or rename this folder (the firewall rule points at the exe's path), delete the `UxPlay` folder or `C:\msys64`, or want to update UxPlay. Editing `start-receiver.bat` doesn't require it.

## Use

1. Double-click `start-receiver.bat`. The receiver runs in the background with no console window and shows a tray icon near the clock. The icon may be under the ^ overflow arrow.
2. On the iPhone, open Control Center → **Screen Mirroring** → **Jaret-PC**.
3. Your phone screen and audio play in a window on the PC.

Only one receiver can run at a time. If you start a second one, a popup tells you one is already running.

## Stopping

Any of these works:

- **Close the video window.** This shuts the receiver down when `EXIT_ON_WINDOW_CLOSE=1` (the default).
- **Right-click the tray icon** → **Stop receiver**.
- **Double-click `stop-receiver.bat`.** Use this as a fallback if the tray icon is missing.

With `EXIT_ON_WINDOW_CLOSE=1`, the receiver also shuts down when you stop mirroring from the iPhone, because UxPlay closes its window then too. Set it to `0` to keep the receiver running in the tray, waiting for the next connection.

## Settings

Edit the top of `start-receiver.bat`. Changes apply the next time you start the receiver.

| Setting | What it does |
| --- | --- |
| `RECEIVER_NAME` | The name shown in the iPhone's Screen Mirroring list. |
| `EXTRA_ARGS` | UxPlay options. `-vs` picks the video output (`glimagesink`, `d3d11videosink` or `d3d12videosink`). Add `-fs` for fullscreen. Run `uxplay.exe -h` for the full list. |
| `EXIT_ON_WINDOW_CLOSE` | `1` = shut down when the video window closes. `0` = keep running in the tray. |

## Logs

UxPlay's output goes to `%TEMP%\airplay-receiver.log`. Right-click the tray icon → **Open log** to view it. If UxPlay fails right at startup, a popup shows the last lines of the log.

## Portable exe (for other PCs)

To build a single `AirPlayReceiver.exe` (~90 MB) that runs on any Windows PC without MSYS2, first run `setup.ps1`, then:

```powershell
powershell -ExecutionPolicy Bypass -File .\packaging\package.ps1
```

The output is `dist\AirPlayReceiver.exe`. Copy it anywhere and double-click it. A window opens with:

- **Status:** a coloured dot and text that update live. Grey means Stopped, green means Running (waiting for an iPhone), and blue means Mirroring. The status also updates if uxplay stops outside the app, for example through `stop-receiver.bat`.
- **Start / Stop:** a button that starts or stops the receiver.
- **Settings:** receiver name, video and audio output, extra arguments, fullscreen, "Stop the receiver when mirroring ends", and "Start the receiver when this app opens". Click **Save** to store them. If the receiver is running and a change needs a restart, the button reads **Save and restart**.

Other behaviour:

- **Closing the window:** while the receiver is running, closing the window keeps it running in the tray. Double-click the tray icon to reopen the window. With the window closed, the app exits when the receiver stops. Use **Exit** in the window or the tray menu to quit completely.
- **One copy at a time:** launching the exe again brings the existing window to the front.
- **First run:** the exe unpacks UxPlay and GStreamer to `%LOCALAPPDATA%\AirPlayReceiver\runtime`, which takes a few seconds. It also asks once to add the "AirPlay Receiver" firewall rule, which needs an admin prompt. If the rule is missing later, the window shows a yellow bar with an **Add rule** button.
- **Settings file:** settings are saved to `AirPlayReceiver.ini` next to the exe.

## Files

- `setup.ps1`: one-time install and build.
- `start-receiver.bat`: your settings. It launches `receiver.ps1`.
- `receiver.ps1`: runs UxPlay hidden, shows the tray icon, and watches the video window.
- `stop-receiver.bat`: force-stops the receiver.
- `packaging\package.ps1`: builds the portable `dist\AirPlayReceiver.exe`.
- `packaging\AirPlayReceiver.cs`: the portable exe's launcher (the C# equivalent of `receiver.ps1`).

## Troubleshooting

- **The PC doesn't show up on the iPhone:**
  - Make sure the phone and PC are on the same network. The PC can be on Ethernet if it uses the same router as the phone's Wi-Fi.
  - Set the Windows network to **Private** (Settings → Network & internet → your connection). The firewall rule only allows connections on Private networks.
  - Check that the firewall rule "UxPlay AirPlay Receiver" exists.
- **Black or choppy video:** change the `-vs` video output in `start-receiver.bat` and try the others.
- **Nothing happens when starting:** open the log (see Logs above), or run `UxPlay\build\uxplay.exe` from an MSYS2 UCRT64 terminal to see errors directly.
