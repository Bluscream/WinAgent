using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WinAgent.Utils;
using WinAgent.Models;

namespace WinAgent;

public static class Program
{
    private static HttpClient _httpClient = null!;
    private static string _baseUrl = null!;
    private static string _token = null!;

    public static async Task<int> Main(string[] args)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);

        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(baseDir, "WinAgent.json"), optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        Config.Initialize(config);

        _token = Config.Get("token", "-token", "WINAGENT_TOKEN");
        if (string.IsNullOrEmpty(_token)) _token = string.Empty;
        var portStr = Config.Get("port", "WINAGENT_PORT");
        if (string.IsNullOrEmpty(portStr)) portStr = "23482";
        _baseUrl = $"http://localhost:{portStr}";

        if (string.IsNullOrEmpty(_token))
        {
            Console.Error.WriteLine("Error: No WINAGENT_TOKEN configured. Set the WINAGENT_TOKEN environment variable or add it to appsettings.json.");
            return 1;
        }

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            ShowHelp();
            return 0;
        }

        try
        {
            if (args.Contains("--state") || args.Contains("--entity-state"))
            {
                var state = GetArgValue(args, "--state") ?? GetArgValue(args, "--entity-state");
                if (string.IsNullOrEmpty(state))
                {
                    Console.Error.WriteLine("Error: --state / --entity-state requires a value.");
                    return 1;
                }
                var attr = GetArgValue(args, "--attributes") ?? GetArgValue(args, "--entity-attributes");
                return await ReportState(state, attr);
            }

            if (args.Contains("--event"))
            {
                var eventJson = GetArgValue(args, "--event");
                if (string.IsNullOrEmpty(eventJson))
                {
                    Console.Error.WriteLine("Error: --event requires a JSON payload or string value.");
                    return 1;
                }
                return await FireEvent(eventJson);
            }

            if (args.Contains("--notify") || args.Contains("-n"))
            {
                return await SendNotification(args);
            }

            if (args.Contains("--start-process") || args.Contains("-p"))
            {
                return await StartProcess(args);
            }

            if (args.Contains("--block-status"))
            {
                return await GetStatus("block-status");
            }

            if (args.Contains("--toggle-block"))
            {
                var val = GetArgValue(args, "--toggle-block");
                if (string.IsNullOrEmpty(val))
                {
                    Console.Error.WriteLine("Error: --toggle-block requires 'true' or 'false'.");
                    return 1;
                }
                return await ToggleSetting("toggle-block", bool.Parse(val));
            }

            if (args.Contains("--force-status"))
            {
                return await GetStatus("force-status");
            }

            if (args.Contains("--toggle-force"))
            {
                var val = GetArgValue(args, "--toggle-force");
                if (string.IsNullOrEmpty(val))
                {
                    Console.Error.WriteLine("Error: --toggle-force requires 'true' or 'false'.");
                    return 1;
                }
                return await ToggleSetting("toggle-force", bool.Parse(val));
            }

            if (args.Contains("--execute") || args.Contains("-e"))
            {
                var action = GetArgValue(args, "--execute") ?? GetArgValue(args, "-e");
                if (string.IsNullOrEmpty(action))
                {
                    Console.Error.WriteLine("Error: --execute requires an action value (lock, reboot, shutdown, logoff).");
                    return 1;
                }
                return await ExecuteAction(action);
            }

            if (args.Contains("--power-schemes"))
            {
                return await GetPowerSchemes();
            }

            if (args.Contains("--set-power-scheme"))
            {
                var scheme = GetArgValue(args, "--set-power-scheme");
                if (string.IsNullOrEmpty(scheme))
                {
                    Console.Error.WriteLine("Error: --set-power-scheme requires a scheme name or GUID.");
                    return 1;
                }
                return await SetPowerScheme(scheme);
            }

            if (args.Contains("--device-list") || args.Contains("-d"))
            {
                var categoriesVal = GetArgValue(args, "--device-list") ?? GetArgValue(args, "-d");
                string[]? categories = null;
                if (!string.IsNullOrEmpty(categoriesVal) && categoriesVal != "all")
                {
                    categories = categoriesVal.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                }
                return await ListDevices(categories);
            }

            Console.Error.WriteLine("Unknown command/arguments. Use --help to see usage.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error communicating with service: {ex.Message}");
            return 2;
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("WinAgent CLI Utility v1.2");
        Console.WriteLine("Usage:");
        Console.WriteLine("  WinAgent.CLI.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --state <val>              Report one-off state reporting");
        Console.WriteLine("    [--attributes <json>]    Add optional attributes for state");
        Console.WriteLine();
        Console.WriteLine("  --notify | -n              Trigger notification");
        Console.WriteLine("    --message <msg>          Notification message (Required)");
        Console.WriteLine("    [--title <val>]          Notification title");
        Console.WriteLine("    [--type <val>]           Type: toast, messagebox, banner, xsoverlay, ovrtoolkit");
        Console.WriteLine("    [--msgbox-type <val>]    MessageBox type: ok, okcancel, yesno, etc.");
        Console.WriteLine("    [--msgbox-icon <val>]    MessageBox icon: info, warning, error, etc.");
        Console.WriteLine("    [--timeout <seconds>]    Notification display timeout");
        Console.WriteLine("    [--flash]                Flash window");
        Console.WriteLine("    [--ding]                 Play sound/ding");
        Console.WriteLine();
        Console.WriteLine("  --start-process | -p       Start a process remotely");
        Console.WriteLine("    --executable <path>      Path to executable or command");
        Console.WriteLine("    [--arguments <val>]      Arguments to pass");
        Console.WriteLine("    [--as-user <session>]    Run in specific user session ID");
        Console.WriteLine("    [--elevated]             Run with elevated permissions");
        Console.WriteLine("    [--wait]                 Wait for exit");
        Console.WriteLine("    [--timeout <ms>]         Timeout when waiting for exit");
        Console.WriteLine();
        Console.WriteLine("  --block-status             Check shutdown blocking status");
        Console.WriteLine("  --toggle-block <t/f>       Enable/disable shutdown blocking");
        Console.WriteLine("  --force-status             Check force action status");
        Console.WriteLine("  --toggle-force <t/f>       Enable/disable force actions");
        Console.WriteLine();
        Console.WriteLine("  --execute | -e <action>    Execute system action: lock, reboot, shutdown, logoff");
        Console.WriteLine();
        Console.WriteLine("  --power-schemes            List all system power schemes");
        Console.WriteLine("  --set-power-scheme <val>   Set active power scheme by name or GUID");
        Console.WriteLine();
        Console.WriteLine("  --device-list | -d [cat]   List PnP devices. Optional comma-separated categories filter.");
        Console.WriteLine();
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    return args[i + 1];
                }
                return string.Empty;
            }
        }
        return null;
    }

    private static async Task<int> ReportState(string state, string? attributes)
    {
        var url = $"{_baseUrl}/api/state?state={Uri.EscapeDataString(state)}";
        if (!string.IsNullOrEmpty(attributes)) url += $"&attributes={Uri.EscapeDataString(attributes)}";

        var resp = await _httpClient.PostAsync(url, null);
        if (resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"State '{state}' reported successfully.");
            return 0;
        }

        Console.Error.WriteLine($"Failed to report state. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> SendNotification(string[] args)
    {
        var message = GetArgValue(args, "--message");
        if (string.IsNullOrEmpty(message))
        {
            Console.Error.WriteLine("Error: --message is required when sending a notification.");
            return 1;
        }

        var request = new NotifyRequest
        {
            Message = message,
            Title = GetArgValue(args, "--title") ?? "WinAgent",
            Type = GetArgValue(args, "--type") ?? "toast",
            MessageBoxType = GetArgValue(args, "--msgbox-type") ?? "ok",
            MessageBoxIcon = GetArgValue(args, "--msgbox-icon") ?? "info",
            Timeout = int.TryParse(GetArgValue(args, "--timeout"), out var t) ? t : 0,
            Flash = args.Contains("--flash"),
            Ding = args.Contains("--ding")
        };

        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/notify", request);
        if (resp.IsSuccessStatusCode)
        {
            Console.WriteLine("Notification sent successfully.");
            return 0;
        }

        Console.Error.WriteLine($"Failed to send notification. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> StartProcess(string[] args)
    {
        var exe = GetArgValue(args, "--executable");
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("Error: --executable is required to start a process.");
            return 1;
        }

        var request = new StartProcessRequest
        {
            Executable = exe,
            Arguments = GetArgValue(args, "--arguments"),
            AsUser = GetArgValue(args, "--as-user"),
            Elevated = args.Contains("--elevated"),
            WaitForExit = args.Contains("--wait"),
            Timeout = int.TryParse(GetArgValue(args, "--timeout"), out var t) ? t : 30000
        };

        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/start-process", request);
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(result);
            return 0;
        }

        Console.Error.WriteLine($"Failed to start process. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> GetStatus(string endpoint)
    {
        var resp = await _httpClient.GetAsync($"{_baseUrl}/api/{endpoint}");
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(content);
            return 0;
        }

        Console.Error.WriteLine($"Failed to get status. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> ToggleSetting(string endpoint, bool enabled)
    {
        var resp = await _httpClient.PostAsync($"{_baseUrl}/api/{endpoint}?enabled={enabled}", null);
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(content);
            return 0;
        }

        Console.Error.WriteLine($"Failed to toggle setting. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> ExecuteAction(string action)
    {
        var resp = await _httpClient.PostAsync($"{_baseUrl}/api/execute?action={action}", null);
        if (resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Action '{action}' triggered successfully.");
            return 0;
        }

        Console.Error.WriteLine($"Failed to execute action. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> GetPowerSchemes()
    {
        var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/power-schemes");
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(content);
            Console.WriteLine("System Power Schemes:");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var activeStr = item.GetProperty("isActive").GetBoolean() ? " [ACTIVE]" : "";
                Console.WriteLine($"  {item.GetProperty("name").GetString()} ({item.GetProperty("guid").GetString()}){activeStr}");
            }
            return 0;
        }

        Console.Error.WriteLine($"Failed to get power schemes. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> SetPowerScheme(string scheme)
    {
        var resp = await _httpClient.PostAsync($"{_baseUrl}/api/system/set-power-scheme?scheme={Uri.EscapeDataString(scheme)}", null);
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(content);
            return 0;
        }

        Console.Error.WriteLine($"Failed to set power scheme. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> ListDevices(string[]? categories)
    {
        var url = $"{_baseUrl}/api/device-list";
        if (categories != null && categories.Length > 0)
        {
            var query = string.Join("&categories=", categories.Select(Uri.EscapeDataString));
            url += $"?categories={query}";
        }

        var resp = await _httpClient.GetAsync(url);
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(content);
            Console.WriteLine("Devices List:");
            foreach (var dev in doc.RootElement.EnumerateArray())
            {
                var enabledStr = dev.GetProperty("enabled").GetBoolean() ? "Enabled" : "Disabled";
                var presentStr = dev.GetProperty("present").GetBoolean() ? "Present" : "Absent";
                Console.WriteLine($"  [{enabledStr} | {presentStr}] {dev.GetProperty("name").GetString()}");
                Console.WriteLine($"    Class: {dev.GetProperty("class").GetString()} | ID: {dev.GetProperty("deviceID").GetString()}");
                Console.WriteLine();
            }
            return 0;
        }

        Console.Error.WriteLine($"Failed to list devices. Status: {resp.StatusCode}");
        return 1;
    }

    private static async Task<int> FireEvent(string eventJson)
    {
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(eventJson).RootElement;
        }
        catch
        {
            var fallback = new Dictionary<string, object> { ["event"] = eventJson };
            element = JsonSerializer.SerializeToElement(fallback);
        }

        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/event", element);
        if (resp.IsSuccessStatusCode)
        {
            Console.WriteLine("Event fired successfully.");
            return 0;
        }

        Console.Error.WriteLine($"Failed to fire event. Status: {resp.StatusCode}");
        return 1;
    }
}
