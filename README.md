# WinAgent (formerly MQTT.Agent)

**Notice:** This project has been fully rebranded and refactored from `MQTT.Agent` to `WinAgent`, and decomposed into a modern modular architecture.

There is **no backward compatibility** with old `MQTTAGENT_*` configurations or services. If you are upgrading, you must:
1. Stop and uninstall the old `MqttAgent` Windows Service.
2. Update all your environment variables from `MQTTAGENT_*` to `WINAGENT_*`.
3. Update your Home Assistant integration from `mqtt_agent` to `win_agent`.
4. Reinstall the service using the new `WinAgent` executable.

---

## Solution Structure

The project has been split into four subprojects under the `WinAgent.slnx` solution:

### 1. [WinAgent.Common](file:///p:/Visual%20Studio/source/repos/WinAgent/WinAgent.Common) (Class Library)
Contains all shared data transfer objects (DTOs), models, P/Invokes, and utility classes. This project is referenced by the other three applications, ensuring namespace and DTO consistency across execution contexts.

### 2. [WinAgent.Service](file:///p:/Visual%20Studio/source/repos/WinAgent/WinAgent.Service) (ASP.NET Core Web Application)
The core backend service that runs either as a console application or a Windows Service. It hosts:
- The REST API endpoint controllers.
- Background monitoring tasks.
- The MQTT manager that connects and reports state to Home Assistant.
- System integration hooks (DXGI screen capture, PnP device controllers, power profiles).

### 3. [WinAgent.Tray](file:///p:/Visual%20Studio/source/repos/WinAgent/WinAgent.Tray) (Windows Forms App)
A lightweight Windows system tray application that acts as a visual interface. It has no direct service layer dependencies, running entirely as an API client that queries and commands the local `WinAgent.Service` through HTTP.

### 4. [WinAgent.CLI](file:///p:/Visual%20Studio/source/repos/WinAgent/WinAgent.CLI) (Console Application)
A command-line interface tool to trigger "one-shot" actions (like sending notifications, switching power schemes, reporting state, or starting processes) remotely via HTTP requests to `WinAgent.Service`.

---

## Build and Run

To build the entire solution:
```powershell
dotnet build WinAgent.slnx
```

To run the background service:
```powershell
dotnet run --project WinAgent.Service
```

To run the system tray:
```powershell
dotnet run --project WinAgent.Tray
```

To run a CLI action:
```powershell
dotnet run --project WinAgent.CLI -- --power-schemes
```
