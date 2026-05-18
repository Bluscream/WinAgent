using System;
using System.Collections.Generic;
using System.Management;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinAgent.Utils;
using WinAgent.Models;
using Microsoft.Extensions.Logging;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace WinAgent.Services;

public class DeviceService
{
    private readonly ILogger<DeviceService> _logger;

    private static readonly Dictionary<string, string> ClassIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Computer", "mdi:computer" },
        { "DiskDrive", "mdi:harddisk" },
        { "Display", "mdi:expansion-card" },
        { "HDC", "mdi:chip" },
        { "Keyboard", "mdi:keyboard" },
        { "Media", "mdi:volume-high" },
        { "Monitor", "mdi:monitor" },
        { "Mouse", "mdi:mouse" },
        { "Net", "mdi:ethernet" },
        { "Ports", "mdi:serial-port" },
        { "Printer", "mdi:printer" },
        { "System", "mdi:cpu-64-bit" },
        { "USB", "mdi:usb" },
        { "Battery", "mdi:battery" },
        { "Bluetooth", "mdi:bluetooth" },
        { "Camera", "mdi:webcam" },
        { "Image", "mdi:camera" },
        { "Biometric", "mdi:fingerprint" },
        { "SmartCardReader", "mdi:smart-card" },
        { "Sensor", "mdi:sensor" },
        { "SoftwareDevice", "mdi:application-cog" },
        { "AudioEndpoint", "mdi:volume-high" },
        { "WPD", "mdi:cellphone" },
        { "Xbox", "mdi:xbox-controller" },
        { "SecurityDevices", "mdi:shield-check" },
        { "PrintQueue", "mdi:printer" }
    };

    public DeviceService(ILogger<DeviceService> logger)
    {
        _logger = logger;
    }

    public string ListDevices(string[]? categories)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            string query = "SELECT Name, DeviceID, PNPClass, ClassGuid, Status, Present, ConfigManagerErrorCode FROM Win32_PnPEntity";
            if (categories != null && categories.Length > 0)
            {
                var escapedCategories = categories.Select(c => $"'{c}'");
                query += $" WHERE PNPClass IN ({string.Join(",", escapedCategories)})";
            }

            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", query);
            foreach (var obj in searcher.Get())
            {
                var errorCodeVal = obj["ConfigManagerErrorCode"];
                uint errorCode = errorCodeVal != null ? Convert.ToUInt32(errorCodeVal) : 0;
                string deviceId = obj["DeviceID"]?.ToString() ?? "";
                string wmiName = obj["Name"]?.ToString() ?? "";

                string pnpClass = obj["PNPClass"]?.ToString() ?? "";
                string resolvedName = wmiName;
                if (!string.IsNullOrEmpty(deviceId))
                {
                    try
                    {
                        var pnpDevice = PnPDevice.GetDeviceByInstanceId(deviceId);
                        resolvedName = ResolveBestDeviceName(pnpDevice, wmiName, pnpClass);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not resolve PnPDevice for {DeviceId}: {Error}", deviceId, ex.Message);
                    }
                }

                ClassIcons.TryGetValue(pnpClass, out var icon);

                devices.Add(new DeviceInfo
                {
                    Name = resolvedName,
                    DeviceID = deviceId,
                    Class = pnpClass,
                    ClassGuid = obj["ClassGuid"]?.ToString() ?? "",
                    Status = obj["Status"]?.ToString() ?? "",
                    Present = (bool)(obj["Present"] ?? false),
                    Enabled = errorCode != 22,
                    Icon = icon
                });
            }
        }
        catch (Exception ex)
        {
             return JsonSerializer.Serialize(new { error = ex.Message });
        }

        return JsonSerializer.Serialize(devices); // Compact JSON
    }

    private string ResolveBestDeviceName(PnPDevice pnpDevice, string wmiName, string pnpClass)
    {
        string? friendlyName = null;
        try { friendlyName = pnpDevice.GetProperty<string>(DevicePropertyKey.Device_FriendlyName); } catch {}

        string? busDesc = null;
        try { busDesc = pnpDevice.GetProperty<string>(DevicePropertyKey.Device_BusReportedDeviceDesc); } catch {}

        string? deviceDesc = null;
        try { deviceDesc = pnpDevice.GetProperty<string>(DevicePropertyKey.Device_DeviceDesc); } catch {}

        string? manufacturer = null;
        try { manufacturer = pnpDevice.GetProperty<string>(DevicePropertyKey.Device_Manufacturer); } catch {}

        // List of generic names we want to avoid or enrich
        var genericKeywords = new[] {
            "generic",
            "standard",
            "usb input device",
            "high definition audio device",
            "usb composite device",
            "pnp monitor",
            "pci bus",
            "hid-compliant",
            "unknown usb device",
            "volume",
            "microsoft basic",
            "bluetooth device",
            "root print queue",
            "composite bus enumerator",
            "virtual hid framework",
            "virtual keyboard",
            "virtual media keys",
            "system timer",
            "motherboard resources",
            "acpi fan",
            "acpi thermal zone",
            "pnp software device enumerator",
            "umbus enumerator",
            "video controller",
            "vga compatible",
            "3d video controller",
            "display controller",
            "pci device",
            "pci simple communications controller"
        };

        bool IsGeneric(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string lower = name.ToLowerInvariant();
            
            // Check if matches generic keywords
            if (genericKeywords.Any(k => lower.Contains(k))) return true;

            // Also treat a device as generic if its name matches its category/class name
            if (!string.IsNullOrWhiteSpace(pnpClass))
            {
                string classLower = pnpClass.ToLowerInvariant();
                if (lower == classLower) return true;
            }

            return false;
        }

        // 1. If we have a non-generic friendly name, use it!
        if (!IsGeneric(friendlyName)) return friendlyName!;

        // 2. If we have a non-generic bus description, use it!
        if (!IsGeneric(busDesc) && busDesc != null)
        {
            if (!string.IsNullOrWhiteSpace(manufacturer) && !busDesc.Contains(manufacturer, StringComparison.OrdinalIgnoreCase))
            {
                return $"{manufacturer} {busDesc}";
            }
            return busDesc;
        }

        // 3. If we have a non-generic device description, use it!
        if (!IsGeneric(deviceDesc) && deviceDesc != null) return deviceDesc;

        // 4. Try combining manufacturer with description or friendly name to make it less generic
        if (!string.IsNullOrWhiteSpace(manufacturer) && manufacturer != "Microsoft")
        {
            string baseName = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName : (!string.IsNullOrWhiteSpace(deviceDesc) ? deviceDesc : wmiName);
            if (baseName != null && !baseName.Contains(manufacturer, StringComparison.OrdinalIgnoreCase))
            {
                return $"{manufacturer} {baseName}";
            }
        }

        // 5. Fallback cascade
        if (!string.IsNullOrWhiteSpace(friendlyName)) return friendlyName!;
        if (!string.IsNullOrWhiteSpace(busDesc)) return busDesc!;
        if (!string.IsNullOrWhiteSpace(deviceDesc)) return deviceDesc!;

        return wmiName;
    }

    public async Task<DeviceToggleResult> ToggleDevices(string[]? enable, string[]? disable)
    {
        var resultObj = new DeviceToggleResult();
        
        enable ??= Array.Empty<string>();
        disable ??= Array.Empty<string>();

        var restart = enable.Intersect(disable, StringComparer.OrdinalIgnoreCase).ToArray();
        var onlyEnable = enable.Except(restart, StringComparer.OrdinalIgnoreCase).ToArray();
        var onlyDisable = disable.Except(restart, StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var pattern in onlyDisable)
        {
            resultObj.Results.AddRange(await RunPnpAction(pattern, "Disable"));
        }

        foreach (var pattern in onlyEnable)
        {
            resultObj.Results.AddRange(await RunPnpAction(pattern, "Enable"));
        }

        foreach (var pattern in restart)
        {
            var matches = await ResolveDevices(pattern);
            if (matches.Count == 0)
            {
                resultObj.Results.Add(new DeviceToggleDetail
                {
                    Name = pattern,
                    DeviceID = "",
                    Action = "Restart",
                    Success = false,
                    Message = $"Could not find any devices matching '{pattern}'."
                });
                continue;
            }

            foreach (var dev in matches)
            {
                var disResult = await SetDeviceState(dev.DeviceID, dev.Name, "Disable");
                if (disResult.Success)
                {
                    await Task.Delay(1000);
                    var enResult = await SetDeviceState(dev.DeviceID, dev.Name, "Enable");
                    enResult.Action = "Restart";
                    if (!enResult.Success)
                    {
                        enResult.Message = $"Restart failed during Enable phase: {enResult.Message}";
                    }
                    resultObj.Results.Add(enResult);
                }
                else
                {
                    disResult.Action = "Restart";
                    disResult.Message = $"Restart failed during Disable phase: {disResult.Message}";
                    resultObj.Results.Add(disResult);
                }
            }
        }

        resultObj.Success = resultObj.Results.All(r => r.Success);
        return resultObj;
    }

    private async Task<List<DeviceInfo>> ResolveDevices(string pattern)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            // Translate glob * to WMI %, and escape backslashes for WQL query parsing
            string wmiPattern = pattern.Replace("\\", "\\\\").Replace("*", "%").Replace("'", "''");
            if (!wmiPattern.Contains("%")) wmiPattern = $"%{wmiPattern}%";

            string query = $"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '{wmiPattern}' OR DeviceID LIKE '{wmiPattern}'";
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", query);
            
            foreach (var obj in searcher.Get())
            {
                devices.Add(new DeviceInfo
                {
                    Name = obj["Name"]?.ToString() ?? "Unknown",
                    DeviceID = obj["DeviceID"]?.ToString() ?? ""
                });
            }
        }
        catch (Exception)
        {
            _logger.LogError("Failed to resolve devices with pattern {Pattern}", pattern);
        }
        return devices;
    }

    private async Task<List<DeviceToggleDetail>> RunPnpAction(string pattern, string action)
    {
        var results = new List<DeviceToggleDetail>();
        var devices = await ResolveDevices(pattern);

        if (devices.Count == 0)
        {
            results.Add(new DeviceToggleDetail
            {
                Name = pattern,
                DeviceID = "",
                Action = action,
                Success = false,
                Message = $"Could not find any devices matching '{pattern}'."
            });
            return results;
        }

        foreach (var dev in devices)
        {
            results.Add(await SetDeviceState(dev.DeviceID, dev.Name, action));
        }

        return results;
    }

    private async Task<DeviceToggleDetail> SetDeviceState(string instanceId, string name, string action)
    {
        var detail = new DeviceToggleDetail
        {
            Name = name,
            DeviceID = instanceId,
            Action = action
        };

        try
        {
            bool enable = action.Equals("Enable", StringComparison.OrdinalIgnoreCase);
            string errorMsg = "";
            
            bool success = await Task.Run(() =>
            {
                return SystemHelper.SetPnpDeviceState(instanceId, enable, out errorMsg);
            });

            detail.Success = success;
            if (success)
            {
                detail.Message = $"Device '{name}' ({instanceId}) {action}d successfully.";
            }
            else
            {
                detail.Error = errorMsg;
                detail.Message = $"Failed to {action} device '{name}' ({instanceId}): {errorMsg}";
                _logger.LogError("Failed to {Action} device {Name} ({InstanceId}): {Error}", action, name, instanceId, errorMsg);
            }
        }
        catch (Exception ex)
        {
            detail.Success = false;
            detail.Error = ex.Message;
            detail.Message = $"Failed to {action} device '{name}': {ex.Message}";
        }

        return detail;
    }
}
