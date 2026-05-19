import logging
import aiohttp
import voluptuous as vol
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.helpers import config_validation as cv, device_registry as dr
from homeassistant.helpers.typing import ConfigType

from .const import (
    DOMAIN,
    CONF_HOST,
    CONF_PORT,
    CONF_TOKEN,
    SERVICE_NOTIFY,
    SERVICE_START_PROCESS,
    SERVICE_LOCK,
    SERVICE_SHUTDOWN,
    SERVICE_REBOOT,
    SERVICE_LOGOUT,
    SERVICE_LOGIN,
    SERVICE_TYPE_LOGON,
    SERVICE_CLEAR_CREDENTIALS,
    SERVICE_AUDIO,
    SERVICE_DISPLAYS,
    SERVICE_DEVICES,
    SERVICE_UPDATE,
    SERVICE_SCREENSHOT,
    SERVICE_EXECUTE_FEATURE,
)

_LOGGER = logging.getLogger(__name__)

# Service schemas
EXECUTE_FEATURE_SCHEMA = vol.Schema({
    vol.Required("path"): cv.string,
    vol.Optional("payload", default={}): vol.Any(dict, None),
})

NOTIFY_SCHEMA = vol.Schema({
    vol.Required("message"): cv.string,
    vol.Optional("title", default="Home Assistant"): cv.string,
    vol.Optional("toast", default=True): cv.boolean,
    vol.Optional("messagebox", default=False): cv.boolean,
    vol.Optional("ovrtoolkit", default=False): cv.boolean,
    vol.Optional("xsoverlay", default=False): cv.boolean,
    vol.Optional("type", default="MB_OK"): cv.string,
    vol.Optional("icon", default="MB_ICONINFORMATION"): cv.string,
    vol.Optional("timeout_ms", default=5000): cv.positive_int,
})

START_PROCESS_SCHEMA = vol.Schema({
    vol.Required("executable"): cv.string,
    vol.Optional("arguments"): cv.string,
    vol.Optional("wait_for_exit", default=False): cv.boolean,
    vol.Optional("timeout", default=30000): cv.positive_int,
    vol.Optional("shell_execute", default=False): cv.boolean,
    vol.Optional("as_user"): cv.string,
    vol.Optional("elevated", default=False): cv.boolean,
    vol.Optional("window_style"): cv.string,
})

LOCK_SCHEMA = vol.Schema({})

SHUTDOWN_SCHEMA = vol.Schema({
    vol.Optional("force", default=False): cv.boolean,
    vol.Optional("timeout", default=0): cv.positive_int,
    vol.Optional("message"): cv.string,
})

REBOOT_SCHEMA = vol.Schema({
    vol.Optional("force", default=False): cv.boolean,
    vol.Optional("timeout", default=0): cv.positive_int,
    vol.Optional("message"): cv.string,
})

LOGOUT_SCHEMA = vol.Schema({
    vol.Optional("all_users", default=False): cv.boolean,
    vol.Optional("message"): cv.string,
    vol.Optional("timeout", default=0): cv.positive_int,
})

LOGIN_SCHEMA = vol.Schema({
    vol.Required("username"): cv.string,
    vol.Required("password"): cv.string,
    vol.Optional("domain", default=""): cv.string,
    vol.Optional("keep_credentials", default=False): cv.boolean,
    vol.Optional("wts_connect", default=False): cv.boolean,
})

TYPE_LOGON_SCHEMA = vol.Schema({
    vol.Required("text"): cv.string,
    vol.Optional("enter", default=True): cv.boolean,
})

CLEAR_CREDENTIALS_SCHEMA = vol.Schema({})

AUDIO_SCHEMA = vol.Schema({
    vol.Optional("enable"): vol.All(cv.ensure_list, [cv.string]),
    vol.Optional("disable"): vol.All(cv.ensure_list, [cv.string]),
    vol.Optional("set_volumes"): vol.Any(dict, None),
})

DISPLAYS_SCHEMA = vol.Schema({
    vol.Required("action"): cv.string,
    vol.Optional("monitor"): cv.string,
    vol.Optional("value"): cv.string,
})

DEVICES_SCHEMA = vol.Schema({
    vol.Optional("enable"): vol.All(cv.ensure_list, [cv.string]),
    vol.Optional("disable"): vol.All(cv.ensure_list, [cv.string]),
    vol.Optional("categories"): vol.All(cv.ensure_list, [cv.string]),
})

UPDATE_SCHEMA = vol.Schema({
    vol.Optional("install", default=False): cv.boolean,
    vol.Optional("reboot_if_needed", default=False): cv.boolean,
})

SCREENSHOT_SCHEMA = vol.Schema({
    vol.Optional("desktop", default="Default"): cv.string,
    vol.Optional("quality", default=75): cv.positive_int,
    vol.Optional("display", default="all"): cv.string,
    vol.Optional("format", default="png"): cv.string,
    vol.Optional("base64", default=False): cv.boolean,
})

async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Set up the WinAgent integration."""
    hass.data.setdefault(DOMAIN, {})
    return True

async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Set up WinAgent from a config entry."""
    hass.data[DOMAIN][entry.entry_id] = entry.data

    async def get_target_config(call: ServiceCall):
        # In a multi-device setup, use the entry associated with this setup.
        return entry.data

    async def call_agent_feature(path: str, payload: dict, call: ServiceCall):
        config = await get_target_config(call)
        host = config[CONF_HOST]
        port = config[CONF_PORT]
        token = config[CONF_TOKEN]
        
        url = f"http://{host}:{port}/api/{path}"
        
        async with aiohttp.ClientSession() as session:
            try:
                headers = {"Authorization": f"Bearer {token}"}
                async with session.post(url, json=payload, headers=headers) as response:
                    if response.status != 200:
                        _LOGGER.error("Failed to execute feature '%s' on %s: %s", path, host, await response.text())
                    else:
                        _LOGGER.info("Successfully executed feature '%s' on %s", path, host)
            except Exception as e:
                _LOGGER.error("Error executing feature '%s' on %s: %s", path, host, str(e))

    # Define HA service handlers mapping to local REST endpoints
    async def handle_execute_feature(call: ServiceCall):
        await call_agent_feature(call.data["path"], call.data.get("payload", {}), call)

    async def handle_notify(call: ServiceCall):
        payload = {
            "title": call.data.get("title", ""),
            "message": call.data["message"],
            "toast": call.data.get("toast", True),
            "messagebox": call.data.get("messagebox", False),
            "ovrtoolkit": call.data.get("ovrtoolkit", False),
            "xsoverlay": call.data.get("xsoverlay", False),
            "type": call.data.get("type", "MB_OK"),
            "icon": call.data.get("icon", "MB_ICONINFORMATION"),
            "timeoutMs": call.data.get("timeout_ms", 5000)
        }
        await call_agent_feature("system/notify", payload, call)

    async def handle_start_process(call: ServiceCall):
        payload = {
            "executable": call.data["executable"],
            "arguments": call.data.get("arguments"),
            "waitForExit": call.data.get("wait_for_exit", False),
            "timeout": call.data.get("timeout", 30000),
            "shellExecute": call.data.get("shell_execute", False),
            "asUser": call.data.get("as_user"),
            "elevated": call.data.get("elevated", False),
            "windowStyle": call.data.get("window_style")
        }
        await call_agent_feature("process/start", payload, call)

    async def handle_lock(call: ServiceCall):
        await call_agent_feature("system/lock", {}, call)

    async def handle_shutdown(call: ServiceCall):
        payload = {
            "force": call.data.get("force", False),
            "timeout": call.data.get("timeout", 0),
            "message": call.data.get("message")
        }
        await call_agent_feature("system/shutdown", payload, call)

    async def handle_reboot(call: ServiceCall):
        payload = {
            "force": call.data.get("force", False),
            "timeout": call.data.get("timeout", 0),
            "message": call.data.get("message")
        }
        await call_agent_feature("system/reboot", payload, call)

    async def handle_logout(call: ServiceCall):
        payload = {
            "allUsers": call.data.get("all_users", False),
            "message": call.data.get("message"),
            "timeout": call.data.get("timeout", 0)
        }
        await call_agent_feature("system/logout", payload, call)

    async def handle_login(call: ServiceCall):
        payload = {
            "username": call.data["username"],
            "password": call.data["password"],
            "domain": call.data.get("domain", ""),
            "keepCredentials": call.data.get("keep_credentials", False),
            "wtsConnect": call.data.get("wts_connect", False)
        }
        await call_agent_feature("system/login", payload, call)

    async def handle_type_logon(call: ServiceCall):
        payload = {
            "text": call.data["text"],
            "enter": call.data.get("enter", True)
        }
        await call_agent_feature("system/type_logon", payload, call)

    async def handle_clear_credentials(call: ServiceCall):
        await call_agent_feature("system/clear_credentials", {}, call)

    async def handle_audio(call: ServiceCall):
        payload = {
            "enable": call.data.get("enable"),
            "disable": call.data.get("disable"),
            "setVolumes": call.data.get("set_volumes")
        }
        await call_agent_feature("system/audio", payload, call)

    async def handle_displays(call: ServiceCall):
        payload = {
            "action": call.data["action"],
            "monitor": call.data.get("monitor"),
            "value": call.data.get("value")
        }
        await call_agent_feature("system/displays", payload, call)

    async def handle_devices(call: ServiceCall):
        payload = {
            "enable": call.data.get("enable"),
            "disable": call.data.get("disable"),
            "categories": call.data.get("categories")
        }
        await call_agent_feature("system/devices", payload, call)

    async def handle_update(call: ServiceCall):
        payload = {
            "install": call.data.get("install", False),
            "rebootIfNeeded": call.data.get("reboot_if_needed", False)
        }
        await call_agent_feature("system/update", payload, call)

    async def handle_screenshot(call: ServiceCall):
        payload = {
            "desktop": call.data.get("desktop", "Default"),
            "quality": call.data.get("quality", 75),
            "display": call.data.get("display", "all"),
            "format": call.data.get("format", "png"),
            "base64": call.data.get("base64", False)
        }
        await call_agent_feature("capture/screenshot", payload, call)

    # Register services
    hass.services.async_register(DOMAIN, SERVICE_EXECUTE_FEATURE, handle_execute_feature, schema=EXECUTE_FEATURE_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_NOTIFY, handle_notify, schema=NOTIFY_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_START_PROCESS, handle_start_process, schema=START_PROCESS_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_LOCK, handle_lock, schema=LOCK_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_SHUTDOWN, handle_shutdown, schema=SHUTDOWN_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_REBOOT, handle_reboot, schema=REBOOT_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_LOGOUT, handle_logout, schema=LOGOUT_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_LOGIN, handle_login, schema=LOGIN_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_TYPE_LOGON, handle_type_logon, schema=TYPE_LOGON_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_CLEAR_CREDENTIALS, handle_clear_credentials, schema=CLEAR_CREDENTIALS_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_AUDIO, handle_audio, schema=AUDIO_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_DISPLAYS, handle_displays, schema=DISPLAYS_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_DEVICES, handle_devices, schema=DEVICES_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_UPDATE, handle_update, schema=UPDATE_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_SCREENSHOT, handle_screenshot, schema=SCREENSHOT_SCHEMA)

    return True

async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Unload a config entry."""
    hass.data[DOMAIN].pop(entry.entry_id)
    return True

