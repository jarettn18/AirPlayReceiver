@echo off
rem Stops the AirPlay receiver if it is running (the tray icon closes by itself).
taskkill /IM uxplay.exe /F >nul 2>&1 && echo Receiver stopped. || echo Receiver was not running.
timeout /t 2 >nul
