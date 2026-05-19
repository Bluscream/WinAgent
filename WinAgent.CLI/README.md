# WinAgent.CLI

A command-line interface wrapper for communicating with the `WinAgent.Service` REST API. It allows executing "one-shot" actions from the command line, scripts, or automations.

## Configuration

Reads configuration in priority order from:
1. `appsettings.json` / `WinAgent.json`
2. Environment variables (`WINAGENT_TOKEN`, `WINAGENT_PORT`)
3. Command-line arguments

Ensure `WINAGENT_TOKEN` is configured or provided, as the CLI will authenticate all requests with this token.

## Usage

```bash
# Report a one-off state to Home Assistant (via the service)
winagent.exe --state "Armed" --attributes "{\"reason\":\"manual\"}"

# Send a toast notification
winagent.exe -n --message "Deployment Successful" --title "Build Server"

# Show a classic Windows MessageBox
winagent.exe -n --message "Proceed with restart?" --type messagebox --msgbox-type yesno

# List all system power schemes
winagent.exe --power-schemes

# Set a power scheme by name or GUID
winagent.exe --set-power-scheme "High performance"

# List PnP devices
winagent.exe -d

# Execute system commands
winagent.exe -e lock
winagent.exe -e reboot
```

## Options

- `--state <val>`: Report one-off entity state value.
  - `[--attributes <json>]`: JSON string of state attributes.
- `--notify | -n`: Trigger notification.
  - `--message <msg>`: Notification message (Required).
  - `[--title <val>]`: Title (default: "WinAgent").
  - `[--type <val>]`: `toast`, `messagebox`, `banner`, `xsoverlay`, `ovrtoolkit`.
  - `[--msgbox-type <val>]`: `ok`, `okcancel`, `yesno`, `yesnocancel`, `retrycancel`.
  - `[--msgbox-icon <val>]`: `info`, `warning`, `error`, `question`, `none`.
  - `[--timeout <seconds>]`: Custom display timeout.
  - `[--flash]`: Flash window.
  - `[--ding]`: Play sound alert.
- `--start-process | -p`: Launch process remotely.
  - `--executable <path>`: Executable path.
  - `[--arguments <args>]`: Arguments.
  - `[--as-user <session>]`: Target user Session ID.
  - `[--elevated]`: Request admin elevation.
  - `[--wait]`: Block until exit.
- `--block-status`: Get shutdown blocking status.
- `--toggle-block <true/false>`: Enable or disable shutdown blocking.
- `--force-status`: Get force action status.
- `--toggle-force <true/false>`: Enable or disable force actions.
- `--execute | -e <action>`: Trigger `lock`, `reboot`, `shutdown`, `logoff`.
- `--power-schemes`: Display power profiles.
- `--set-power-scheme <name/guid>`: Activate profile.
- `--device-list | -d [categories]`: List devices.
