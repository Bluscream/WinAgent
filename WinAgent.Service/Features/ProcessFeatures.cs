using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record StartProcessRequest(
    string Executable = "", 
    string? Arguments = null, 
    bool WaitForExit = false, 
    int Timeout = 30000, 
    bool ShellExecute = false, 
    string? AsUser = null, 
    bool Elevated = false, 
    string? WindowStyle = null
);

[Feature(Path = "process/start", Description = "Start an application. Can optionally execute as shell command (redirect output and return it), wait for exit, run as specific user, or run elevated.")]
public class StartProcessFeature : BaseFeature<StartProcessRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(StartProcessRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<ProcessService>();
        var result = await service.StartProcess(
            request.Executable, 
            request.Arguments, 
            request.WaitForExit, 
            request.Timeout, 
            request.ShellExecute, 
            request.AsUser, 
            request.Elevated, 
            request.WindowStyle
        );
        return FeatureResult.FromText(result);
    }
}

public record ListRequest(
    bool Processes = false,
    bool Windows = false,
    bool Com = false,
    bool Pipes = false,
    bool Services = false,
    bool Mmfs = false,
    bool Desktops = false,
    bool Users = false,
    bool Audio = false,
    bool Displays = false,
    bool Monitors = false,
    bool Screens = false,
    bool Devices = false,
    string[]? RegistryPaths = null,
    string RegistryHive = "HKEY_CURRENT_USER",
    string? DeviceCategories = null,
    int ProcessTimeout = 30000
);

[Feature(Path = "system/list", Description = "List available IPC and system resources as a compact JSON object.")]
public class ListFeature : BaseFeature<ListRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ListRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var result = new Dictionary<string, object>();

        // List Processes
        if (request.Processes)
        {
            try
            {
                var processService = services.GetRequiredService<ProcessService>();
                var processText = processService.ListProcesses(request.ProcessTimeout);
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
        if (request.Windows)
        {
            try
            {
                var windowsService = services.GetRequiredService<WindowsService>();
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
        if (request.Com)
        {
            try
            {
                var comService = services.GetRequiredService<ComService>();
                result["COM Objects"] = comService.ListComObjects();
            }
            catch (Exception ex)
            {
                result["COM Objects"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Named Pipes
        if (request.Pipes)
        {
            try
            {
                var namedPipeService = services.GetRequiredService<NamedPipeService>();
                result["Named Pipes"] = namedPipeService.ListNamedPipes();
            }
            catch (Exception ex)
            {
                result["Named Pipes"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Services
        if (request.Services)
        {
            try
            {
                var serviceService = services.GetRequiredService<ServiceService>();
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
        if (request.Mmfs)
        {
            try
            {
                var mmfService = services.GetRequiredService<MemoryMappedFileService>();
                result["Memory-Mapped Files"] = mmfService.ListMappedFiles();
            }
            catch (Exception ex)
            {
                result["Memory-Mapped Files"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Desktops
        if (request.Desktops)
        {
            try
            {
                var screenshotService = services.GetRequiredService<ScreenshotService>();
                result["Desktops"] = screenshotService.ListDesktops();
            }
            catch (Exception ex)
            {
                result["Desktops"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Users
        if (request.Users)
        {
            try
            {
                var logonService = services.GetRequiredService<LogonRegistryService>();
                result["Users"] = logonService.ListUsers();
            }
            catch (Exception ex)
            {
                result["Users"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Audio
        if (request.Audio)
        {
            try
            {
                var audioService = services.GetRequiredService<AudioService>();
                result["Audio Devices"] = audioService.ListAudioDevices();
            }
            catch (Exception ex)
            {
                result["Audio Devices"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Displays/Monitors/Screens
        if (request.Displays || request.Monitors || request.Screens)
        {
            try
            {
                var monitorService = services.GetRequiredService<MultiMonitorToolService>();
                var json = await monitorService.GetMonitorsAsync("json");
                result["Monitors"] = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new List<Dictionary<string, string>>();
            }
            catch (Exception ex)
            {
                result["Monitors"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Devices
        if (request.Devices)
        {
            try
            {
                var deviceService = services.GetRequiredService<DeviceService>();
                string[]? cats = string.IsNullOrEmpty(request.DeviceCategories) ? null : request.DeviceCategories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                result["Devices"] = deviceService.ListDevices(cats);
            }
            catch (Exception ex)
            {
                result["Devices"] = new[] { new { Error = ex.Message } };
            }
        }

        // List Registry Paths
        if (request.RegistryPaths != null && request.RegistryPaths.Length > 0)
        {
            try
            {
                var registryService = services.GetRequiredService<RegistryService>();
                var registryResults = new Dictionary<string, object>();
                foreach (var path in request.RegistryPaths)
                {
                    try { registryResults[path] = registryService.ReadRegistry(path, null, request.RegistryHive); }
                    catch (Exception ex) { registryResults[path] = new { Error = ex.Message }; }
                }
                result["Registry"] = registryResults;
            }
            catch (Exception ex)
            {
                result["Registry"] = new[] { new { Error = ex.Message } };
            }
        }

        return FeatureResult.FromJson(result);
    }
}
