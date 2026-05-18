# WinAgent.Common

Shared class library containing models, utilities, and helpers used by both `WinAgent.Service` and `WinAgent.Tray`.

## Contents

### Models
- **MqttOptions** – MQTT broker configuration
- **NotifyRequest / ToastPayload** – Notification request/response DTOs
- **NeedsAttentionInfo** – Window attention tracking
- **DeviceModels** – `DeviceInfo`, `DeviceToggleResult`, `DeviceToggleDetail`

### Utils
- **Global** – Application-wide constants, flags, and argument parsing
- **Config** – Centralized configuration access (appsettings, env vars, CLI args)
- **Extensions** – Extension methods for hardware sensors, WMI, processes, strings, JSON, etc.
- **NativeMethods** – P/Invoke declarations for Win32 APIs
- **ServiceHelper** – Windows Service management (install, start, stop, query)
- **SessionHelper** – Session-0 helper utilities
- **SystemHelper** – System-level operations (PnP device state, window management)
- **Capture/** – DXGI and GDI screen capture backends

## Dependencies
- LibreHardwareMonitorLib
- Microsoft.Data.Sqlite
- Microsoft.Extensions.Configuration.Abstractions
- System.Management
- Nefarius.Utilities.DeviceManagement
- Vortice.Direct3D11 / DXGI / Mathematics
