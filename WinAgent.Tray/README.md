# WinAgent.Tray

Lightweight WinForms system tray application that provides a UI for interacting with the WinAgent service.

## Features
- **System tray icon** with context menu for quick actions
- **Service monitoring** – shows service status and auto-prompts to start if down
- **Power controls** – shutdown, reboot, logoff, lock (with block/force toggles)
- **Power profile switching** via `powercfg`
- **Device management** – enable/disable PnP devices from the tray
- **All actions route through the Service API** – no direct service dependencies

## Architecture
The Tray app communicates with `WinAgent.Service` exclusively via HTTP calls to `localhost:{port}`. It does not host any background services or MQTT connections, keeping its memory footprint minimal.

## Configuration
Reads the same config sources as the Service to determine the API port and token:
- `appsettings.json` / `WinAgent.json`
- Environment variables (`WINAGENT_TOKEN`, `WINAGENT_PORT`)
- Command-line arguments

## Dependencies
- WinAgent.Common (shared models/utils)
- Microsoft.Extensions.Configuration
