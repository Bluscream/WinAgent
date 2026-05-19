using System.ComponentModel;
using ModelContextProtocol.Server;
using WinAgent.Services;
using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace WinAgent.Tools;

[McpServerToolType]
public static partial class IpcTools
{
    private static T GetService<T>(IServiceProvider serviceProvider) where T : class
    {
        return serviceProvider.GetRequiredService<T>();
    }

    private static string FormatError(string toolName, Exception ex)
    {
        var errorMessage = $"Failed to execute {toolName}!\n\n{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
        {
            errorMessage += $"\n\nInner Exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        }
        return errorMessage;
    }

    [McpServerTool, Description("Interact with a named pipe. If pattern is set to '.*', waits for a response. Otherwise, just waits for the pipe to exist. Optionally sends a message first.")]
    public static async Task<string> NamedPipe(
        IServiceProvider serviceProvider,
        [Description("Name of the pipe")] string pipeName,
        [Description("Optional message to send first")] string? message = null,
        [Description("Timeout in milliseconds")] int timeout = 30000,
        [Description("Check interval in milliseconds when waiting for pipe")] int checkInterval = 500,
        [Description("Regex pattern to filter messages. Set to '.*' to wait for any response. If null, just waits for pipe to exist.")] string? pattern = null)
    {
        try
        {
            var service = GetService<NamedPipeService>(serviceProvider);
            return await service.NamedPipe(pipeName, message, timeout, checkInterval, pattern);
        }
        catch (Exception ex)
        {
            return FormatError("named_pipe", ex);
        }
    }


    [McpServerTool, Description("Read from or write to a memory-mapped file. If message is provided, writes to the file. Otherwise, reads from it.")]
    public static string MappedFile(
        IServiceProvider serviceProvider,
        [Description("Name of the memory-mapped file")] string mapName,
        [Description("Optional message to write. If not provided, reads from the file.")] string? message = null,
        [Description("Offset to start reading/writing from")] long offset = 0,
        [Description("Number of bytes to read (only used when reading)")] int length = 4096)
    {
        try
        {
            var service = GetService<MemoryMappedFileService>(serviceProvider);
            return service.MappedFile(mapName, message, offset, length);
        }
        catch (Exception ex)
        {
            return FormatError("mapped_file", ex);
        }
    }

    [McpServerTool, Description("Read from or write to a specific Windows registry value. To list keys or values in a directory, use the 'list' tool with 'registryPaths' instead.")]
    public static string Registry(
        IServiceProvider serviceProvider,
        [Description("Registry key path (e.g., 'Software\\MyApp\\Settings')")] string keyPath,
        [Description("Registry value name (optional, if not provided, reads default value)")] string? valueName = null,
        [Description("Value to write (optional, if not provided, reads from registry)")] string? value = null,
        [Description("Value type for writing (String, DWord, QWord, Binary, MultiString, ExpandString)")] string valueType = "String",
        [Description("Registry hive (HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, HKEY_CLASSES_ROOT, HKEY_USERS, HKEY_CURRENT_CONFIG)")] string hive = "HKEY_CURRENT_USER")
    {
        try
        {
            var service = GetService<RegistryService>(serviceProvider);
            
            if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(valueName))
            {
                // Write mode
                return service.WriteRegistry(keyPath, valueName, value, valueType, hive);
            }
            else
            {
                // Read mode
                return service.ReadRegistry(keyPath, valueName, hive);
            }
        }
        catch (Exception ex)
        {
            return FormatError("registry", ex);
        }
    }


    [McpServerTool, Description("Query a specific COM object's registration or call its methods. To list all registered COM objects, use the 'list' tool with 'com: true' instead.")]
    public static string Com(
        IServiceProvider serviceProvider,
        [Description("COM ProgID (required to query or call)")] string? progId = null,
        [Description("Method to call (optional, if not provided, queries the COM object)")] string? method = null,
        [Description("Parameters as JSON string (only used when calling a method)")] string? parameters = null,
        [Description("CLSID to query (alternative to ProgID)")] string? clsid = null)
    {
        try
        {
            var service = GetService<ComService>(serviceProvider);
            
            // If method is provided, call it
            if (!string.IsNullOrEmpty(method) && !string.IsNullOrEmpty(progId))
            {
                Dictionary<string, object>? paramsDict = null;
                
                if (!string.IsNullOrEmpty(parameters))
                {
                    paramsDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(parameters);
                }
                
                return service.SendComMessage(progId, method, paramsDict);
            }
            else
            {
                // Query COM database
                return service.QueryComObject(progId, clsid);
            }
        }
        catch (Exception ex)
        {
            return FormatError("com", ex);
        }
    }

    [McpServerTool, Description("Search the Windows registry using glob patterns, similar to regedit search. Supports searching in key names, value names, and value data with multithreaded performance.")]
    public static string SearchRegistry(
        IServiceProvider serviceProvider,
        [Description("Glob pattern to search for (e.g., '*test*', '*.exe')")] string query,
        [Description("Registry path to start search from (optional, searches from hive root if not provided)")] string? path = null,
        [Description("Whether to search in key names")] bool searchKeys = true,
        [Description("Whether to search in value names")] bool searchValues = true,
        [Description("Whether to search in value data")] bool searchData = true,
        [Description("Registry hive (HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, HKEY_CLASSES_ROOT, HKEY_USERS, HKEY_CURRENT_CONFIG)")] string hive = "HKEY_CURRENT_USER")
    {
        try
        {
            var service = GetService<RegistryService>(serviceProvider);
            return service.SearchRegistry(query, path, searchKeys, searchValues, searchData, hive);
        }
        catch (Exception ex)
        {
            return FormatError("search_registry", ex);
        }
    }

    [McpServerTool, Description("Start an application. Can optionally execute as shell command (redirect output and return it), wait for exit, run as specific user, or run elevated.")]
    public static async Task<string> StartProcess(
        IServiceProvider serviceProvider,
        [Description("Path to executable or application name")] string executable,
        [Description("Command line arguments")] string? arguments = null,
        [Description("Whether to wait for the process to exit")] bool waitForExit = false,
        [Description("Timeout in milliseconds if waiting for exit. Use -1 for infinite timeout (no timeout).")] int timeout = 30000,
        [Description("If true, executes as shell command (redirects output, waits for completion, and returns output). If false, starts process normally.")] bool shellExecute = false,
        [Description("Run as specific user (username or session ID). The MCP client should determine the user. If not specified, runs as SYSTEM or current user.")] string? asUser = null,
        [Description("Run with elevation (UAC prompt will appear). Cannot be combined with asUser.")] bool elevated = false,
        [Description("Window style for the process (Normal, Hidden, Minimized, Maximized). Defaults to Normal.")] string? windowStyle = null)
    {
        try
        {
            var service = GetService<ProcessService>(serviceProvider);
            return await service.StartProcess(executable, arguments, waitForExit, timeout, shellExecute, asUser, elevated, windowStyle);
        }
        catch (Exception ex)
        {
            return FormatError("start_process", ex);
        }
    }


    [McpServerTool, Description("List available IPC and system resources as a compact JSON object. Categories must be explicitly requested via boolean flags.")]
    public static string List(
        IServiceProvider serviceProvider,
        [Description("If true, list running processes")] bool processes = false,
        [Description("If true, list open windows")] bool windows = false,
        [Description("If true, list registered COM objects")] bool com = false,
        [Description("If true, list active named pipes")] bool pipes = false,
        [Description("If true, list Windows services")] bool services = false,
        [Description("If true, list memory-mapped files")] bool mmfs = false,
        [Description("If true, list all desktops on WinSta0")] bool desktops = false,
        [Description("If true, list discovered user accounts")] bool users = false,
        [Description("If true, list audio input/output devices")] bool audio = false,
        [Description("If true, list active displays")] bool displays = false,
        [Description("If true, list active monitors")] bool monitors = false,
        [Description("If true, list active screens")] bool screens = false,
        [Description("If true, list PnP devices")] bool devices = false,
        [Description("List of registry key paths to query for values and subkeys")] string[]? registryPaths = null,
        [Description("Registry hive for registryPaths (default: HKEY_CURRENT_USER)")] string registryHive = "HKEY_CURRENT_USER",
        [Description("Check interval for PnP devices or list categories (comma separated)")] string? deviceCategories = null,
        [Description("Timeout in milliseconds for listing processes")] int processTimeout = 30000)
    {
        try
        {
            var result = new Dictionary<string, object>();

            // List Processes
            if (processes)
            {
                try
                {
                    var processService = GetService<ProcessService>(serviceProvider);
                    var processText = processService.ListProcesses(processTimeout);
                    var processesList = new List<Dictionary<string, string>>();
                    
                    var lines = processText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 2) // Skip header lines
                    {
                        for (int i = 2; i < lines.Length; i++)
                        {
                            var parts = lines[i].Split('\t');
                            if (parts.Length >= 3)
                            {
                                processesList.Add(new Dictionary<string, string>
                                {
                                    { "PID", parts[0] },
                                    { "Name", parts[1] },
                                    { "CommandLine", parts.Length > 2 ? parts[2] : "" }
                                });
                            }
                        }
                    }
                    result["Processes"] = processesList;
                }
                catch (Exception ex)
                {
                    result["Processes"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Windows
            if (windows)
            {
                try
                {
                    var windowsService = GetService<WindowsService>(serviceProvider);
                    var windowsList = windowsService.ListWindows();
                    result["Windows"] = windowsList.Select(w => new Dictionary<string, object>
                    {
                        { "Handle", w.Handle.ToString() },
                        { "Title", w.Title },
                        { "ClassName", w.ClassName },
                        { "ProcessId", w.ProcessId },
                        { "ThreadId", w.ThreadId },
                        { "IsVisible", w.IsVisible },
                        { "IsEnabled", w.IsEnabled },
                        { "X", w.X },
                        { "Y", w.Y },
                        { "Width", w.Width },
                        { "Height", w.Height }
                    }).ToList();
                }
                catch (Exception ex)
                {
                    result["Windows"] = new[] { new { Error = ex.Message } };
                }
            }

            // List COM Objects
            if (com)
            {
                try
                {
                    var comService = GetService<ComService>(serviceProvider);
                    result["COM Objects"] = comService.ListComObjects();
                }
                catch (Exception ex)
                {
                    result["COM Objects"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Named Pipes
            if (pipes)
            {
                try
                {
                    var namedPipeService = GetService<NamedPipeService>(serviceProvider);
                    result["Named Pipes"] = namedPipeService.ListNamedPipes();
                }
                catch (Exception ex)
                {
                    result["Named Pipes"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Services
            if (services)
            {
                try
                {
                    var serviceService = GetService<ServiceService>(serviceProvider);
                    var serviceText = serviceService.ListServices();
                    var servicesList = new List<Dictionary<string, string>>();
                    
                    var lines = serviceText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 2) // Skip header lines
                    {
                        for (int i = 2; i < lines.Length; i++)
                        {
                            var parts = lines[i].Split('\t');
                            if (parts.Length >= 4)
                            {
                                servicesList.Add(new Dictionary<string, string>
                                {
                                    { "Name", parts[0] },
                                    { "DisplayName", parts[1] },
                                    { "Status", parts[2] },
                                    { "StartType", parts[3] }
                                });
                            }
                        }
                    }
                    result["Services"] = servicesList;
                }
                catch (Exception ex)
                {
                    result["Services"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Memory-Mapped Files
            if (mmfs)
            {
                try
                {
                    var mmfService = GetService<MemoryMappedFileService>(serviceProvider);
                    result["Memory-Mapped Files"] = mmfService.ListMappedFiles();
                }
                catch (Exception ex)
                {
                    result["Memory-Mapped Files"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Desktops
            if (desktops)
            {
                try
                {
                    var screenshotService = GetService<ScreenshotService>(serviceProvider);
                    result["Desktops"] = screenshotService.ListDesktops();
                }
                catch (Exception ex)
                {
                    result["Desktops"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Users
            if (users)
            {
                try
                {
                    var logonService = GetService<LogonRegistryService>(serviceProvider);
                    result["Users"] = logonService.ListUsers();
                }
                catch (Exception ex)
                {
                    result["Users"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Audio
            if (audio)
            {
                try
                {
                    var audioService = GetService<AudioService>(serviceProvider);
                    result["Audio Devices"] = audioService.ListAudioDevices();
                }
                catch (Exception ex)
                {
                    result["Audio Devices"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Displays/Monitors/Screens
            if (displays || monitors || screens)
            {
                try
                {
                    var monitorService = GetService<MultiMonitorToolService>(serviceProvider);
                    var json = monitorService.GetMonitorsAsync("json").GetAwaiter().GetResult();
                    result["Monitors"] = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new List<Dictionary<string, string>>();
                }
                catch (Exception ex)
                {
                    result["Monitors"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Devices
            if (devices)
            {
                try
                {
                    var deviceService = GetService<DeviceService>(serviceProvider);
                    string[]? cats = string.IsNullOrEmpty(deviceCategories) ? null : deviceCategories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    result["Devices"] = deviceService.ListDevices(cats);
                }
                catch (Exception ex)
                {
                    result["Devices"] = new[] { new { Error = ex.Message } };
                }
            }

            // List Registry Paths
            if (registryPaths != null && registryPaths.Length > 0)
            {
                try
                {
                    var registryService = GetService<RegistryService>(serviceProvider);
                    var registryResults = new Dictionary<string, object>();
                    foreach (var path in registryPaths)
                    {
                        try { registryResults[path] = registryService.ReadRegistry(path, null, registryHive); }
                        catch (Exception ex) { registryResults[path] = new { Error = ex.Message }; }
                    }
                    result["Registry"] = registryResults;
                }
                catch (Exception ex)
                {
                    result["Registry"] = new[] { new { Error = ex.Message } };
                }
            }

            return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        }
        catch (Exception ex)
        {
            return FormatError("list", ex);
        }
    }

    [McpServerTool, Description("Log in a local Windows user from the login screen using username/password. Note: this stores the password temporarily in the registry.")]
    public static string Login(
        IServiceProvider serviceProvider,
        [Description("Local Windows username")] string username,
        [Description("User's password")] string password,
        [Description("Domain (optional, use hostname for local account)")] string domain = "",
        [Description("If true, do not clear the password from registry after login (Dangerous!)")] bool keepCredentials = false,
        [Description("If true, try to forcibly connect the session to the console after login")] bool wtsConnect = false)
    {
        try
        {
            var service = GetService<LogonRegistryService>(serviceProvider);
            return service.Login(username, password, domain, keepCredentials, wtsConnect);
        }
        catch (Exception ex)
        {
            return FormatError("login", ex);
        }
    }

    [McpServerTool, Description("Locks the current Windows workstation.")]
    public static async Task<string> Lock(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<WindowsService>(serviceProvider);
            return await service.Lock();
        }
        catch (Exception ex)
        {
            return FormatError("lock", ex);
        }
    }

    [McpServerTool, Description("Types text into the active logon session.")]
    public static async Task<string> TypeLogon(
        IServiceProvider serviceProvider,
        [Description("The text to type (e.g. a PIN or password)")] string text,
        [Description("Whether to press Enter after typing")] bool enter = true)
    {
        try
        {
            var service = GetService<LogonRegistryService>(serviceProvider);
            return await service.TypeLogon(text, enter);
        }
        catch (Exception ex)
        {
            return FormatError("type_logon", ex);
        }
    }

    [McpServerTool, Description("Clears any staged auto-logon credentials from the registry.")]
    public static async Task<string> ClearCredentials(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<LogonRegistryService>(serviceProvider);
            return await service.ClearCredentials();
        }
        catch (Exception ex)
        {
            return FormatError("clear_credentials", ex);
        }
    }

    [McpServerTool, Description("Logs out the current user or all users.")]
    public static string Logout(
        IServiceProvider serviceProvider,
        [Description("If true, logs out all active user sessions")] bool allUsers = false,
        [Description("Optional message to display before logout")] string? message = null,
        [Description("Delay in seconds before logout")] int timeout = 0)
    {
        try
        {
            var service = GetService<WindowsService>(serviceProvider);
            return service.Logout(allUsers, message, timeout);
        }
        catch (Exception ex)
        {
            return FormatError("logout", ex);
        }
    }

    [McpServerTool, Description("Shuts down the system.")]
    public static string Shutdown(
        IServiceProvider serviceProvider,
        [Description("If true, forces applications to close")] bool force = false,
        [Description("Delay in seconds before shutdown")] int timeout = 0,
        [Description("Optional message to display")] string? message = null)
    {
        try
        {
            var service = GetService<WindowsService>(serviceProvider);
            return service.Shutdown(reboot: false, force: force, timeout: timeout, message: message);
        }
        catch (Exception ex)
        {
            return FormatError("shutdown", ex);
        }
    }

    [McpServerTool, Description("Reboots the system.")]
    public static string Reboot(
        IServiceProvider serviceProvider,
        [Description("If true, forces applications to close")] bool force = false,
        [Description("Delay in seconds before reboot")] int timeout = 0,
        [Description("Optional message to display")] string? message = null)
    {
        try
        {
            var service = GetService<WindowsService>(serviceProvider);
            return service.Shutdown(reboot: true, force: force, timeout: timeout, message: message);
        }
        catch (Exception ex)
        {
            return FormatError("reboot", ex);
        }
    }

    [McpServerTool, Description("Stops the IPC MCP service.")]
    public static string StopMcp(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<McpService>(serviceProvider);
            return service.StopMcp();
        }
        catch (Exception ex)
        {
            return FormatError("stop_mcp", ex);
        }
    }

    [McpServerTool, Description("Restarts the IPC MCP service gracefully.")]
    public static string RestartMcp(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<McpService>(serviceProvider);
            return service.RestartMcp();
        }
        catch (Exception ex)
        {
            return FormatError("restart_mcp", ex);
        }
    }

    [McpServerTool, Description("Restarts the Windows Update service and triggers a search for updates.")]
    public static string Update(
        IServiceProvider serviceProvider,
        [Description("If true, download and install updates automatically")] bool install = false,
        [Description("If true, reboot if needed after installation")] bool rebootIfNeeded = false)
    {
        try
        {
            var service = GetService<UpdateService>(serviceProvider);
            return service.Update(install, rebootIfNeeded);
        }
        catch (Exception ex)
        {
            return FormatError("update", ex);
        }
    }

    [McpServerTool, Description("Control audio input/output devices (enable, disable, volume). To list devices, use the 'list' tool with 'audio: true' instead.")]
    public static string Audio(
        IServiceProvider serviceProvider,
        [Description("List of device names or IDs to enable")] string[]? enable = null,
        [Description("List of device names or IDs to disable")] string[]? disable = null,
        [Description("Dictionary of device name/ID to volume percentage (0-100)")] Dictionary<string, int>? set_volumes = null)
    {
        try
        {
            var service = GetService<AudioService>(serviceProvider);
            
            // Handle set_volumes
            if (set_volumes != null)
            {
                foreach (var kvp in set_volumes)
                {
                    service.SetAudioVolume(kvp.Key, kvp.Value);
                }
            }

            // Handle enable/disable
            if (enable != null)
            {
                foreach (var id in enable) service.ToggleAudioDevice(id, true);
            }
            if (disable != null)
            {
                foreach (var id in disable) service.ToggleAudioDevice(id, false);
            }

            return service.ListAudioDevices();
        }
        catch (Exception ex)
        {
            return FormatError("audio", ex);
        }
    }

    [McpServerTool, Description("Control monitors using MultiMonitorTool. Possible actions: list, enable, disable, switch, setprimary, setorientation, setscale, setmax, turnoff, turnon, switchoffon, movealltoprimary.")]
    public static async Task<string> Displays(
        IServiceProvider serviceProvider,
        [Description("Action to perform")] string action,
        [Description("Monitor ID, Name, or Serial Number (required for most actions)")] string? monitor = null,
        [Description("Value for actions like setorientation (0, 90, 180, 270) or setscale (e.g. 150)")] string? value = null)
    {
        try
        {
            var service = GetService<MultiMonitorToolService>(serviceProvider);
            return action.ToLower() switch
            {
                "list" => await service.GetMonitorsAsync("json"),
                "enable" => await service.EnableAsync(monitor ?? throw new ArgumentException("Monitor is required")),
                "disable" => await service.DisableAsync(monitor ?? throw new ArgumentException("Monitor is required")),
                "switch" => await service.SwitchAsync(monitor ?? throw new ArgumentException("Monitor is required")),
                "setprimary" => await service.SetPrimaryAsync(monitor ?? throw new ArgumentException("Monitor is required")),
                "setorientation" => await service.SetOrientationAsync(monitor ?? throw new ArgumentException("Monitor is required"), int.Parse(value ?? "0")),
                "setscale" => await service.SetScaleAsync(monitor ?? throw new ArgumentException("Monitor is required"), int.Parse(value ?? "100")),
                "setmax" => await service.SetMaxResolutionAsync(monitor ?? throw new ArgumentException("Monitor is required")),
                "turnoff" => await service.TurnOffMonitorsAsync(),
                "turnon" => await service.TurnOnMonitorsAsync(),
                "switchoffon" => await service.SwitchOffOnMonitorsAsync(),
                "movealltoprimary" => await service.MoveAllWindowsToPrimaryAsync(),
                _ => throw new ArgumentException($"Unknown action: {action}")
            };
        }
        catch (Exception ex)
        {
            return FormatError("displays", ex);
        }
    }

    [McpServerTool, Description("Control PnP devices (enable, disable, restart). To list devices, use the 'list' tool with 'devices: true' instead.")]
    public static async Task<string> Devices(
        IServiceProvider serviceProvider,
        [Description("List of device names or IDs to enable")] string[]? enable = null,
        [Description("List of device names or IDs to disable")] string[]? disable = null,
        [Description("List of device categories to filter list output (e.g. 'Monitor', 'Net', 'Mouse')")] string[]? categories = null)
    {
        try
        {
            var service = serviceProvider.GetRequiredService<DeviceService>();
            
            if ((enable != null && enable.Length > 0) || (disable != null && disable.Length > 0))
            {
                await service.ToggleDevices(enable, disable);
            }

            return service.ListDevices(categories);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.ToString();
        }
    }

    [McpServerTool, Description("Send notifications to various targets (Desktop Toast, MessageBox, OVRToolkit, XSOverlay).")]
    public static async Task<string> Notify(
        IServiceProvider serviceProvider,
        [Description("Notification Title")] string title,
        [Description("Notification Text")] string message,
        [Description("Whether to show a Windows Toast notification")] bool toast = false,
        [Description("Whether to show a MessageBox (Modern TaskDialog if available)")] bool messagebox = false,
        [Description("Whether to send to OVRToolkit")] bool ovrtoolkit = false,
        [Description("Whether to send to XSOverlay")] bool xsoverlay = false,
        [Description("MessageBox button type (e.g. MB_OK, MB_YESNO)")] string type = "MB_OK",
        [Description("MessageBox icon type (e.g. MB_ICONINFORMATION, MB_ICONWARNING)")] string icon = "MB_ICONINFORMATION",
        [Description("Timeout in milliseconds")] int timeout_ms = 5000)
    {
        try
        {
            var service = GetService<NotifyService>(serviceProvider);
            return await service.NotifyAsync(title, message, toast, messagebox, ovrtoolkit, xsoverlay, type, icon, timeout_ms);
        }
        catch (Exception ex)
        {
            return FormatError("notify", ex);
        }
    }
    [McpServerTool, Description("Capture a screenshot (JPEG or transparent PNG). Returns a base64-encoded image string.")]
    public static async Task<string> Screenshot(
        IServiceProvider serviceProvider,
        [Description("Name of the desktop to capture (e.g. 'Default', 'Winlogon')")] string desktop = "Default",
        [Description("Output format (jpeg, png)")] string format = "jpeg",
        [Description("JPEG quality (1-100)")] int quality = 75,
        [Description("Physical screen to capture (index 0, 1, or 'all' for stitching)")] string display = "0")
    {
        try
        {
            var service = GetService<ScreenshotService>(serviceProvider);
            var bytes = await service.CaptureScreenshot(desktop, quality, display, format);
            if (bytes == null || bytes.Length == 0)
            {
                return "ERROR: Screenshot capture returned empty or null bytes.";
            }
            bool usePng = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);
            string mimeType = usePng ? "image/png" : "image/jpeg";
            return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            return FormatError("screenshot", ex);
        }
    }

    [McpServerTool, Description("List all available physical screens/monitors.")]
    public static async Task<string> ListScreens(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<ScreenshotService>(serviceProvider);
            return System.Text.Json.JsonSerializer.Serialize(await service.ListScreens());
        }
        catch (Exception ex)
        {
            return FormatError("listscreens", ex);
        }
    }

    // ────────── Consolidated Router Tool ──────────

    [McpServerTool, Description("Universal entry point for all WinAgent tools. Use argument 'tool' = 'list' or any other tool name to invoke it.")]
    public static async Task<string> Tool(
        IServiceProvider serviceProvider,
        [Description("The tool name to invoke (e.g. 'screenshot', 'lock', 'list', 'displays')")] string tool,
        [Description("JSON-formatted arguments for the specified tool")] string? arguments = null)
    {
        try
        {
            return await DispatchTool(serviceProvider, tool, arguments);
        }
        catch (Exception ex)
        {
            return FormatError("tool", ex);
        }
    }

    [McpServerTool, Description("Alias/wrapper tool to dispatch UEVR/Universal tools. Use argument 'tool' to specify the internal action.")]
    public static async Task<string> Uevr(
        IServiceProvider serviceProvider,
        [Description("The tool name to invoke (e.g. 'screenshot', 'lock', 'list')")] string tool,
        [Description("JSON-formatted arguments for the specified tool")] string? arguments = null)
    {
        try
        {
            return await DispatchTool(serviceProvider, tool, arguments);
        }
        catch (Exception ex)
        {
            return FormatError("uevr", ex);
        }
    }

    private static async Task<string> DispatchTool(IServiceProvider serviceProvider, string tool, string? arguments = null)
    {
        var elem = ParseArguments(arguments);
        var normTool = tool.Replace("-", "_").Replace(" ", "_").ToLowerInvariant();

        switch (normTool)
        {
            // Hardware monitor tools
            case "list_hardware":
            case "listhardware":
                return HardwareTools.ListHardware(serviceProvider, GetStringArg(elem, "hardwareType"));
            case "get_sensors":
            case "getsensors":
                return HardwareTools.GetSensors(
                    serviceProvider, 
                    GetStringArg(elem, "hardwareIdentifier"), 
                    GetStringArg(elem, "sensorType"), 
                    GetStringArg(elem, "hardwareType")
                );
            case "get_sensor_value":
            case "getsensorvalue":
                return HardwareTools.GetSensorValue(serviceProvider, GetStringArg(elem, "sensorIdentifier") ?? throw new ArgumentException("sensorIdentifier is required"));
            case "get_hardware_detail":
            case "gethardwaredetail":
                return HardwareTools.GetHardwareDetail(serviceProvider, GetStringArg(elem, "hardwareIdentifier") ?? throw new ArgumentException("hardwareIdentifier is required"));
            case "get_system_summary":
            case "getsystemsummary":
                return HardwareTools.GetSystemSummary(serviceProvider);
            case "get_cpu_info":
            case "getcpuinfo":
                return HardwareTools.GetCpuInfo(serviceProvider);
            case "get_gpu_info":
            case "getgpuinfo":
                return HardwareTools.GetGpuInfo(serviceProvider);
            case "get_memory_info":
            case "getmemoryinfo":
                return HardwareTools.GetMemoryInfo(serviceProvider);
            case "get_storage_info":
            case "getstorageinfo":
                return HardwareTools.GetStorageInfo(serviceProvider);
            case "get_network_info":
            case "getnetworkinfo":
                return HardwareTools.GetNetworkInfo(serviceProvider);
            case "get_battery_info":
            case "getbatteryinfo":
                return HardwareTools.GetBatteryInfo(serviceProvider);
            case "get_fan_info":
            case "getfaninfo":
                return HardwareTools.GetFanInfo(serviceProvider);
            case "get_power_info":
            case "getpowerinfo":
                return HardwareTools.GetPowerInfo(serviceProvider);

            // IPC and System tools
            case "named_pipe":
            case "namedpipe":
                return await NamedPipe(
                    serviceProvider,
                    GetStringArg(elem, "pipeName") ?? throw new ArgumentException("pipeName is required"),
                    GetStringArg(elem, "message"),
                    GetIntArg(elem, "timeout", 30000),
                    GetIntArg(elem, "checkInterval", 500),
                    GetStringArg(elem, "pattern")
                );
            case "mapped_file":
            case "mappedfile":
                return MappedFile(
                    serviceProvider,
                    GetStringArg(elem, "mapName") ?? throw new ArgumentException("mapName is required"),
                    GetStringArg(elem, "message"),
                    GetLongArg(elem, "offset", 0),
                    GetIntArg(elem, "length", 4096)
                );
            case "registry":
                return Registry(
                    serviceProvider,
                    GetStringArg(elem, "keyPath") ?? throw new ArgumentException("keyPath is required"),
                    GetStringArg(elem, "valueName"),
                    GetStringArg(elem, "value"),
                    GetStringArg(elem, "valueType", "String") ?? "String",
                    GetStringArg(elem, "hive", "HKEY_CURRENT_USER") ?? "HKEY_CURRENT_USER"
                );
            case "com":
                return Com(
                    serviceProvider,
                    GetStringArg(elem, "progId"),
                    GetStringArg(elem, "method"),
                    GetStringArg(elem, "parameters"),
                    GetStringArg(elem, "clsid")
                );
            case "search_registry":
            case "searchregistry":
                return SearchRegistry(
                    serviceProvider,
                    GetStringArg(elem, "query") ?? throw new ArgumentException("query is required"),
                    GetStringArg(elem, "path"),
                    GetBoolArg(elem, "searchKeys", true),
                    GetBoolArg(elem, "searchValues", true),
                    GetBoolArg(elem, "searchData", true),
                    GetStringArg(elem, "hive", "HKEY_CURRENT_USER") ?? "HKEY_CURRENT_USER"
                );
            case "start_process":
            case "startprocess":
                return await StartProcess(
                    serviceProvider,
                    GetStringArg(elem, "executable") ?? throw new ArgumentException("executable is required"),
                    GetStringArg(elem, "arguments"),
                    GetBoolArg(elem, "waitForExit", false),
                    GetIntArg(elem, "timeout", 30000),
                    GetBoolArg(elem, "shellExecute", false),
                    GetStringArg(elem, "asUser"),
                    GetBoolArg(elem, "elevated", false),
                    GetStringArg(elem, "windowStyle")
                );
            case "list":
                return List(
                    serviceProvider,
                    GetBoolArg(elem, "processes", false),
                    GetBoolArg(elem, "windows", false),
                    GetBoolArg(elem, "com", false),
                    GetBoolArg(elem, "pipes", false),
                    GetBoolArg(elem, "services", false),
                    GetBoolArg(elem, "mmfs", false),
                    GetBoolArg(elem, "desktops", false),
                    GetBoolArg(elem, "users", false),
                    GetBoolArg(elem, "audio", false),
                    GetBoolArg(elem, "displays", false),
                    GetBoolArg(elem, "monitors", false),
                    GetBoolArg(elem, "screens", false),
                    GetBoolArg(elem, "devices", false),
                    GetStringArrayArg(elem, "registryPaths"),
                    GetStringArg(elem, "registryHive", "HKEY_CURRENT_USER") ?? "HKEY_CURRENT_USER",
                    GetStringArg(elem, "deviceCategories"),
                    GetIntArg(elem, "processTimeout", 30000)
                );
            case "login":
                return Login(
                    serviceProvider,
                    GetStringArg(elem, "username") ?? throw new ArgumentException("username is required"),
                    GetStringArg(elem, "password") ?? throw new ArgumentException("password is required"),
                    GetStringArg(elem, "domain", "") ?? "",
                    GetBoolArg(elem, "keepCredentials", false),
                    GetBoolArg(elem, "wtsConnect", false)
                );
            case "lock":
                return await Lock(serviceProvider);
            case "type_logon":
            case "typelogon":
                return await TypeLogon(
                    serviceProvider,
                    GetStringArg(elem, "text") ?? throw new ArgumentException("text is required"),
                    GetBoolArg(elem, "enter", true)
                );
            case "clear_credentials":
            case "clearcredentials":
                return await ClearCredentials(serviceProvider);
            case "logout":
                return Logout(
                    serviceProvider,
                    GetBoolArg(elem, "allUsers", false),
                    GetStringArg(elem, "message"),
                    GetIntArg(elem, "timeout", 0)
                );
            case "shutdown":
                return Shutdown(
                    serviceProvider,
                    GetBoolArg(elem, "force", false),
                    GetIntArg(elem, "timeout", 0),
                    GetStringArg(elem, "message")
                );
            case "reboot":
                return Reboot(
                    serviceProvider,
                    GetBoolArg(elem, "force", false),
                    GetIntArg(elem, "timeout", 0),
                    GetStringArg(elem, "message")
                );
            case "stop_mcp":
            case "stopmcp":
                return StopMcp(serviceProvider);
            case "restart_mcp":
            case "restartmcp":
                return RestartMcp(serviceProvider);
            case "update":
                return Update(
                    serviceProvider,
                    GetBoolArg(elem, "install", false),
                    GetBoolArg(elem, "rebootIfNeeded", false)
                );
            case "audio":
                return Audio(
                    serviceProvider,
                    GetStringArrayArg(elem, "enable"),
                    GetStringArrayArg(elem, "disable"),
                    GetStringIntDictArg(elem, "set_volumes")
                );
            case "displays":
                return await Displays(
                    serviceProvider,
                    GetStringArg(elem, "action") ?? throw new ArgumentException("action is required"),
                    GetStringArg(elem, "monitor"),
                    GetStringArg(elem, "value")
                );
            case "devices":
                return await Devices(
                    serviceProvider,
                    GetStringArrayArg(elem, "enable"),
                    GetStringArrayArg(elem, "disable"),
                    GetStringArrayArg(elem, "categories")
                );
            case "notify":
                return await Notify(
                    serviceProvider,
                    GetStringArg(elem, "title") ?? throw new ArgumentException("title is required"),
                    GetStringArg(elem, "message") ?? throw new ArgumentException("message is required"),
                    GetBoolArg(elem, "toast", false),
                    GetBoolArg(elem, "messagebox", false),
                    GetBoolArg(elem, "ovrtoolkit", false),
                    GetBoolArg(elem, "xsoverlay", false),
                    GetStringArg(elem, "type", "MB_OK") ?? "MB_OK",
                    GetStringArg(elem, "icon", "MB_ICONINFORMATION") ?? "MB_ICONINFORMATION",
                    GetIntArg(elem, "timeout_ms", 5000)
                );
            case "screenshot":
                return await Screenshot(
                    serviceProvider,
                    GetStringArg(elem, "desktop", "Default") ?? "Default",
                    GetStringArg(elem, "format", "jpeg") ?? "jpeg",
                    GetIntArg(elem, "quality", 75),
                    GetStringArg(elem, "display", "0") ?? "0"
                );
            case "listscreens":
            case "list_screens":
                return await ListScreens(serviceProvider);

            default:
                throw new ArgumentException($"Unknown tool name: {tool}");
        }
    }

    private static JsonElement? ParseArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return null;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringArg(JsonElement? elem, string name, string? defaultValue = null)
    {
        if (elem == null || elem.Value.ValueKind != JsonValueKind.Object) return defaultValue;
        if (elem.Value.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            if (prop.ValueKind == JsonValueKind.Null) return null;
            return prop.GetRawText().Trim('"'); // Fallback
        }
        return defaultValue;
    }

    private static bool GetBoolArg(JsonElement? elem, string name, bool defaultValue = false)
    {
        if (elem == null || elem.Value.ValueKind != JsonValueKind.Object) return defaultValue;
        if (elem.Value.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var b)) return b;
        }
        return defaultValue;
    }

    private static int GetIntArg(JsonElement? elem, string name, int defaultValue = 0)
    {
        if (elem == null || elem.Value.ValueKind != JsonValueKind.Object) return defaultValue;
        if (elem.Value.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)) return val;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var sVal)) return sVal;
        }
        return defaultValue;
    }

    private static long GetLongArg(JsonElement? elem, string name, long defaultValue = 0)
    {
        if (elem == null || elem.Value.ValueKind != JsonValueKind.Object) return defaultValue;
        if (elem.Value.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val)) return val;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var sVal)) return sVal;
        }
        return defaultValue;
    }

    private static string[]? GetStringArrayArg(JsonElement? elem, string name)
    {
        if (elem == null || elem.Value.ValueKind != JsonValueKind.Object) return null;
        if (elem.Value.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString() ?? "");
                    else
                        list.Add(item.GetRawText().Trim('"'));
                }
                return list.ToArray();
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                return new[] { prop.GetString() ?? "" };
            }
        }
        return null;
    }

    private static Dictionary<string, int>? GetStringIntDictArg(JsonElement? elem, string name)
    {
        if (elem == null || elem.Value.ValueKind != JsonValueKind.Object) return null;
        if (elem.Value.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, int>();
                foreach (var item in prop.EnumerateObject())
                {
                    if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var val))
                        dict[item.Name] = val;
                    else if (item.Value.ValueKind == JsonValueKind.String && int.TryParse(item.Value.GetString(), out var sVal))
                        dict[item.Name] = sVal;
                }
                return dict;
            }
        }
        return null;
    }
}
