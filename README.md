# WinAgent (formerly MQTT.Agent)

**Notice:** This project has been fully rebranded and refactored from `MQTT.Agent` to `WinAgent`.
All configuration values, environment variables, services, and integrations have been updated to reflect this new branding.
There is **no backward compatibility** with old `MQTTAGENT_*` configurations or services.
If you are upgrading from `MQTT.Agent`, you must:
1. Stop and uninstall the old `MqttAgent` Windows Service.
2. Update all your environment variables from `MQTTAGENT_*` to `WINAGENT_*`.
3. Update your Home Assistant integration from `mqtt_agent` to `win_agent`.
4. Reinstall the service using the new `WinAgent` executable.

## Overview
WinAgent is a Windows Agent for providing MQTT, MCP, and API control.
