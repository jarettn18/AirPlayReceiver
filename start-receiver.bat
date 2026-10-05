@echo off
rem Starts the AirPlay receiver. On the iPhone: Control Center -> Screen Mirroring -> pick RECEIVER_NAME.
rem It runs in the background with a tray icon (near the clock); right-click it to stop.

set "RECEIVER_NAME=Jaret-PC"
rem Add -fs to start fullscreen. Other video outputs: d3d11videosink, d3d12videosink.
set "EXTRA_ARGS=-vs glimagesink -as wasapisink"
rem 1 = shut down when the video window closes (you close it, or the iPhone stops mirroring).
rem 0 = keep running in the tray and wait for the next connection.
set "EXIT_ON_WINDOW_CLOSE=1"

start "" powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0receiver.ps1"
