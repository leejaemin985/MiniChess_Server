@echo off
title MiniChess Server (port 5055, LAN)
cd /d "%~dp0"

echo [MiniChess] Starting server on all network interfaces (port 5055)...
echo [MiniChess] This PC: ws://localhost:5055/ws
echo [MiniChess] Other PCs on the same network connect to one of these IPs with :5055
ipconfig | findstr /c:"IPv4"
echo [MiniChess] Stop: Ctrl+C or close this window
echo.

dotnet run --project src\MiniChess.Server --launch-profile http -- --urls http://0.0.0.0:5055

echo.
echo [MiniChess] Server stopped.
pause
