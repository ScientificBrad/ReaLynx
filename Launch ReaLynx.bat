@echo off
start "" "%~dp0ReaLynx\ReaLynx.Win\bin\Release\net8.0-windows\ReaLynx.Win.exe" --boot "%~dp0BOOTROM\lynxboot.img" %*
