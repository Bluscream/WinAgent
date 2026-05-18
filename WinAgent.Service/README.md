# WinAgent.Service

ASP.NET Core Web API application that runs as a Windows Service. Hosts the background worker services, MQTT integration, and REST API controllers.

## Features
- **REST API** with Swagger UI at `/docs`
- **Token authentication** (Bearer token via header or query parameter)
- **MQTT integration** for Home Assistant discovery and state reporting
- **Background services**: system monitoring, shutdown blocking, notification receiving, action execution
- **Windows Service support** via `Microsoft.Extensions.Hosting.WindowsServices`

## Running

```bash
# Run as console app
dotnet run

# Install as Windows Service (elevated)
WinAgent.Service.exe --install

# Uninstall
WinAgent.Service.exe --uninstall
```

## Configuration

Configuration is loaded from (in priority order):
1. `appsettings.json`
2. `WinAgent.json`
3. Environment variables (`WINAGENT_TOKEN`, `WINAGENT_PORT`, `MQTT_*`)
4. Command-line arguments

### Required
- `WINAGENT_TOKEN` – Authentication token for API access

### Optional
- `WINAGENT_PORT` – API port (default: `23482`)
- `MQTT_IP` / `MQTT_PORT` / `MQTT_USER` / `MQTT_PW` – MQTT broker settings

## Dependencies
- WinAgent.Common (shared models/utils)
- MQTTnet
- Serilog
- Swashbuckle (Swagger)
- NAudio, Vortice (capture)
- TaskScheduler
