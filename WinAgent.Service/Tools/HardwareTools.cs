using System.ComponentModel;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using ModelContextProtocol.Server;
using WinAgent.Services;

namespace WinAgent.Tools;

/// <summary>
/// MCP tool implementations for LibreHardwareMonitor hardware monitoring.
/// Provides system health data: temperatures, loads, fans, power, voltages, etc.
/// </summary>
[McpServerToolType]
public static class HardwareTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static T GetService<T>(IServiceProvider serviceProvider) where T : class
        => serviceProvider.GetRequiredService<T>();

    private static string FormatError(string toolName, Exception ex)
    {
        var errorMessage = $"Failed to execute {toolName}!\n\n{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
            errorMessage += $"\n\nInner Exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        return errorMessage;
    }

    // ────────── MCP Tool Methods ──────────

    [McpServerTool, Description("List all detected hardware components (CPU, GPU, motherboard, storage, memory, network, battery, etc.) with their types and identifiers. Use the identifiers to query specific hardware sensors.")]
    public static string ListHardware(
        IServiceProvider serviceProvider,
        [Description("Optional filter by hardware type: Motherboard, SuperIO, Cpu, Memory, GpuNvidia, GpuAmd, GpuIntel, Storage, Network, Cooler, EmbeddedController, Psu, Battery")] string? hardwareType = null)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            HardwareType? filterType = null;
            if (!string.IsNullOrEmpty(hardwareType) && Enum.TryParse<HardwareType>(hardwareType, true, out var parsed))
                filterType = parsed;

            var hardware = new List<object>();
            CollectHardwareInfo(service.GetHardware(), hardware, filterType);
            return JsonSerializer.Serialize(new { hardware }, JsonOpts);
        }
        catch (Exception ex) { return FormatError("list_hardware", ex); }
    }

    [McpServerTool, Description("Get sensor readings (temperature, load, clock, voltage, fan speed, power, etc.) with optional filters. Returns current values structured as a tree-like dictionary.")]
    public static string GetSensors(
        IServiceProvider serviceProvider,
        [Description("Filter by hardware identifier (e.g. '/cpu/0', '/gpu-nvidia/0'). Get identifiers from list_hardware.")] string? hardwareIdentifier = null,
        [Description("Filter by sensor type: Voltage, Current, Power, Clock, Temperature, Load, Frequency, Fan, Flow, Control, Level, Factor, Data, SmallData, Throughput, TimeSpan, Timing, Energy, Noise, Conductivity, Humidity")] string? sensorType = null,
        [Description("Filter by hardware type: Motherboard, SuperIO, Cpu, Memory, GpuNvidia, GpuAmd, GpuIntel, Storage, Network, Cooler, EmbeddedController, Psu, Battery")] string? hardwareType = null)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();

            SensorType? sType = null;
            HardwareType? hwType = null;

            if (!string.IsNullOrEmpty(sensorType) && Enum.TryParse<SensorType>(sensorType, true, out var st))
                sType = st;
            if (!string.IsNullOrEmpty(hardwareType) && Enum.TryParse<HardwareType>(hardwareType, true, out var ht))
                hwType = ht;

            var sensors = service.GetAllSensors(hardwareIdentifier, sType, hwType);
            var tree = FormatSensorsTree(sensors);
            return JsonSerializer.Serialize(tree, JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_sensors", ex); }
    }

    [McpServerTool, Description("Get the current value of a specific sensor by its identifier. Returns the value, min, and max recorded values.")]
    public static string GetSensorValue(
        IServiceProvider serviceProvider,
        [Description("The sensor identifier (e.g. '/cpu/0/temperature/0'). Get identifiers from get_sensors.")] string sensorIdentifier)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();

            var sensor = service.FindSensor(sensorIdentifier)
                ?? throw new ArgumentException($"Sensor not found: {sensorIdentifier}");

            return JsonSerializer.Serialize(new
            {
                identifier = sensor.Identifier.ToString(),
                name = sensor.Name,
                hardware = sensor.Hardware.Name,
                hardwareType = sensor.Hardware.HardwareType.ToString(),
                sensorType = sensor.SensorType.ToString(),
                value = sensor.Value,
                min = sensor.Min,
                max = sensor.Max,
                unit = GetUnit(sensor.SensorType),
                formatted = FormatValue(sensor.Value, sensor.SensorType)
            }, JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_sensor_value", ex); }
    }

    [McpServerTool, Description("Get detailed information about a specific hardware component including all its sensors and sub-hardware.")]
    public static string GetHardwareDetail(
        IServiceProvider serviceProvider,
        [Description("The hardware identifier (e.g. '/cpu/0'). Get identifiers from list_hardware.")] string hardwareIdentifier)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();

            var hw = service.FindHardware(hardwareIdentifier)
                ?? throw new ArgumentException($"Hardware not found: {hardwareIdentifier}");

            return JsonSerializer.Serialize(FormatHardwareDetail(hw), JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_hardware_detail", ex); }
    }

    [McpServerTool, Description("Get a high-level summary of the entire system: CPU, GPU, RAM, storage, temperatures, loads, and fan speeds. Great for a quick system health overview.")]
    public static string GetSystemSummary(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            var allSensors = service.GetAllSensors();
            var hardware = service.GetHardware();

            var summary = new Dictionary<string, object>();

            // Hardware list
            var hwList = new List<object>();
            foreach (var hw in hardware)
                hwList.Add(new { name = hw.Name, type = hw.HardwareType.ToString(), identifier = hw.Identifier.ToString() });
            summary["hardware"] = hwList;

            // Key temperatures
            var temps = allSensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F1}°C" })
                .ToList();
            summary["temperatures"] = temps;

            // Key loads
            var loads = allSensors
                .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue && (s.Name.Contains("Total") || s.Name.Contains("CPU Total") || s.Name.Contains("Memory") || s.Name.Contains("GPU Core")))
                .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F1}%" })
                .ToList();
            if (!loads.Any())
            {
                loads = allSensors
                    .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue)
                    .Take(10)
                    .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F1}%" })
                    .ToList();
            }
            summary["loads"] = loads;

            // Fan speeds
            var fans = allSensors
                .Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue)
                .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F0} RPM" })
                .ToList();
            summary["fans"] = fans;

            // Power
            var power = allSensors
                .Where(s => s.SensorType == SensorType.Power && s.Value.HasValue)
                .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F1} W" })
                .ToList();
            summary["power"] = power;

            // Memory
            var memory = allSensors
                .Where(s => (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Load)
                            && s.Hardware.HardwareType == HardwareType.Memory && s.Value.HasValue)
                .Select(s => new { name = s.Name, value = FormatValue(s.Value, s.SensorType) })
                .ToList();
            summary["memory"] = memory;

            return JsonSerializer.Serialize(summary, JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_system_summary", ex); }
    }

    [McpServerTool, Description("Get detailed CPU information including all cores' temperatures, clocks, loads, and power consumption.")]
    public static string GetCpuInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            return GetHardwareTypeInfo(service, HardwareType.Cpu, "cpu");
        }
        catch (Exception ex) { return FormatError("get_cpu_info", ex); }
    }

    [McpServerTool, Description("Get detailed GPU information including temperature, core/memory clocks, VRAM usage, load, fan speed, and power.")]
    public static string GetGpuInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            var result = new List<object>();
            foreach (var hw in service.GetHardware())
            {
                if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    result.Add(FormatHardwareDetail(hw));
            }
            if (!result.Any())
                return JsonSerializer.Serialize(new { message = "No GPU hardware detected" }, JsonOpts);
            return JsonSerializer.Serialize(new { gpus = result }, JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_gpu_info", ex); }
    }

    [McpServerTool, Description("Get RAM information including total/used/available memory, load percentage, and individual stick details if available.")]
    public static string GetMemoryInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            return GetHardwareTypeInfo(service, HardwareType.Memory, "memory");
        }
        catch (Exception ex) { return FormatError("get_memory_info", ex); }
    }

    [McpServerTool, Description("Get storage device information including temperatures, read/write rates, health status, and usage for all drives.")]
    public static string GetStorageInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            return GetHardwareTypeInfo(service, HardwareType.Storage, "storage");
        }
        catch (Exception ex) { return FormatError("get_storage_info", ex); }
    }

    [McpServerTool, Description("Get network adapter information including upload/download throughput and data usage.")]
    public static string GetNetworkInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            return GetHardwareTypeInfo(service, HardwareType.Network, "network");
        }
        catch (Exception ex) { return FormatError("get_network_info", ex); }
    }

    [McpServerTool, Description("Get battery information including charge level, voltage, current, and remaining capacity (for laptops/UPS).")]
    public static string GetBatteryInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            return GetHardwareTypeInfo(service, HardwareType.Battery, "battery");
        }
        catch (Exception ex) { return FormatError("get_battery_info", ex); }
    }

    [McpServerTool, Description("Get all fan speeds and fan controller information across all hardware components.")]
    public static string GetFanInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            var allSensors = service.GetAllSensors(sensorType: SensorType.Fan);
            var controlSensors = service.GetAllSensors(sensorType: SensorType.Control);

            var fans = allSensors.Select(s => FormatSensor(s)).ToList();
            var controls = controlSensors.Select(s => FormatSensor(s)).ToList();

            return JsonSerializer.Serialize(new { fans, controls }, JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_fan_info", ex); }
    }

    [McpServerTool, Description("Get comprehensive power information: voltages (V), wattages (W), and currents (A) across all hardware, grouped by component with per-hardware and system-wide totals.")]
    public static string GetPowerInfo(IServiceProvider serviceProvider)
    {
        try
        {
            var service = GetService<HardwareMonitorService>(serviceProvider);
            service.Update();
            var allSensors = service.GetAllSensors();

            var voltageSensors = allSensors.Where(s => s.SensorType == SensorType.Voltage && s.Value.HasValue).ToList();
            var powerSensors = allSensors.Where(s => s.SensorType == SensorType.Power && s.Value.HasValue).ToList();
            var currentSensors = allSensors.Where(s => s.SensorType == SensorType.Current && s.Value.HasValue).ToList();
            var energySensors = allSensors.Where(s => s.SensorType == SensorType.Energy && s.Value.HasValue).ToList();

            // Group by hardware component
            var byHardware = new Dictionary<string, object>();
            var hwNames = voltageSensors.Select(s => s.Hardware.Name)
                .Union(powerSensors.Select(s => s.Hardware.Name))
                .Union(currentSensors.Select(s => s.Hardware.Name))
                .Union(energySensors.Select(s => s.Hardware.Name))
                .Distinct();

            foreach (var hwName in hwNames)
            {
                var hwVoltages = voltageSensors.Where(s => s.Hardware.Name == hwName).Select(s => FormatSensor(s)).ToList();
                var hwPower = powerSensors.Where(s => s.Hardware.Name == hwName).Select(s => FormatSensor(s)).ToList();
                var hwCurrents = currentSensors.Where(s => s.Hardware.Name == hwName).Select(s => FormatSensor(s)).ToList();
                var hwEnergy = energySensors.Where(s => s.Hardware.Name == hwName).Select(s => FormatSensor(s)).ToList();

                var hwPowerSum = powerSensors.Where(s => s.Hardware.Name == hwName).Sum(s => s.Value ?? 0);
                var hwCurrentSum = currentSensors.Where(s => s.Hardware.Name == hwName).Sum(s => s.Value ?? 0);

                var entry = new Dictionary<string, object>();
                if (hwVoltages.Any()) entry["voltages"] = hwVoltages;
                if (hwPower.Any())
                {
                    entry["power"] = hwPower;
                    entry["powerTotalW"] = Math.Round(hwPowerSum, 2);
                }
                if (hwCurrents.Any())
                {
                    entry["currents"] = hwCurrents;
                    entry["currentTotalA"] = Math.Round(hwCurrentSum, 3);
                }
                if (hwEnergy.Any()) entry["energy"] = hwEnergy;

                byHardware[hwName] = entry;
            }

            // System-wide totals
            var totalPowerW = powerSensors.Sum(s => s.Value ?? 0);
            var totalCurrentA = currentSensors.Sum(s => s.Value ?? 0);
            var voltageAvg = voltageSensors.Any() ? voltageSensors.Average(s => s.Value ?? 0) : 0;

            var totals = new Dictionary<string, object>
            {
                ["totalPowerW"] = Math.Round(totalPowerW, 2),
                ["totalPowerFormatted"] = $"{totalPowerW:F1} W",
                ["totalCurrentA"] = Math.Round(totalCurrentA, 3),
                ["totalCurrentFormatted"] = $"{totalCurrentA:F3} A",
                ["averageVoltageV"] = Math.Round(voltageAvg, 3),
                ["sensorCounts"] = new Dictionary<string, int>
                {
                    ["voltage"] = voltageSensors.Count,
                    ["power"] = powerSensors.Count,
                    ["current"] = currentSensors.Count,
                    ["energy"] = energySensors.Count
                }
            };

            return JsonSerializer.Serialize(new { byHardware, totals }, JsonOpts);
        }
        catch (Exception ex) { return FormatError("get_power_info", ex); }
    }

    // ────────── Helpers ──────────

    private static string GetHardwareTypeInfo(HardwareMonitorService service, HardwareType type, string key)
    {
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == type)
                result.Add(FormatHardwareDetail(hw));
            // Check sub-hardware too
            foreach (var sub in hw.SubHardware)
            {
                if (sub.HardwareType == type)
                    result.Add(FormatHardwareDetail(sub));
            }
        }
        if (!result.Any())
            return JsonSerializer.Serialize(new { message = $"No {key} hardware detected" }, JsonOpts);
        return JsonSerializer.Serialize(new Dictionary<string, object> { [key] = result }, JsonOpts);
    }

    private static void CollectHardwareInfo(IList<IHardware> hardwareList, List<object> result, HardwareType? filter)
    {
        foreach (var hw in hardwareList)
        {
            if (!filter.HasValue || hw.HardwareType == filter.Value)
            {
                result.Add(new
                {
                    name = hw.Name,
                    type = hw.HardwareType.ToString(),
                    identifier = hw.Identifier.ToString(),
                    sensorCount = hw.Sensors.Length,
                    subHardwareCount = hw.SubHardware.Count()
                });
            }
            CollectHardwareInfo(hw.SubHardware, result, filter);
        }
    }

    private static object FormatHardwareDetail(IHardware hw)
    {
        var sensorsByType = hw.Sensors
            .GroupBy(s => s.SensorType)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key.ToString(),
                g => g.Select(s => FormatSensor(s)).ToList()
            );

        var subHardware = new List<object>();
        foreach (var sub in hw.SubHardware)
            subHardware.Add(FormatHardwareDetail(sub));

        return new
        {
            name = hw.Name,
            type = hw.HardwareType.ToString(),
            identifier = hw.Identifier.ToString(),
            sensors = sensorsByType,
            subHardware = subHardware.Any() ? subHardware : null
        };
    }

    private static float? CleanFloat(float? val)
    {
        if (!val.HasValue || float.IsNaN(val.Value) || float.IsInfinity(val.Value))
            return null;
        return val.Value;
    }

    public static object FormatSensorsTree(IEnumerable<ISensor> sensors)
    {
        var groupedByHardware = sensors.GroupBy(s => s.Hardware);
        var hardwareDict = new Dictionary<string, object>();

        foreach (var hwGroup in groupedByHardware)
        {
            var hw = hwGroup.Key;

            var sensorsByType = hwGroup
                .GroupBy(s => s.SensorType)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key.ToString(),
                    g => g.Select(s => new
                    {
                        identifier = s.Identifier.ToString(),
                        name = s.Name,
                        value = CleanFloat(s.Value),
                        min = CleanFloat(s.Min),
                        max = CleanFloat(s.Max),
                        unit = GetUnit(s.SensorType),
                        formatted = FormatValue(CleanFloat(s.Value), s.SensorType)
                    }).ToList()
                );

            hardwareDict[hw.Identifier.ToString()] = new
            {
                name = hw.Name,
                type = hw.HardwareType.ToString(),
                sensors = sensorsByType
            };
        }

        return hardwareDict;
    }

    public static object FormatSensor(ISensor sensor)
    {
        var val = CleanFloat(sensor.Value);
        return new
        {
            identifier = sensor.Identifier.ToString(),
            name = sensor.Name,
            hardware = sensor.Hardware.Name,
            type = sensor.SensorType.ToString(),
            value = val,
            min = CleanFloat(sensor.Min),
            max = CleanFloat(sensor.Max),
            unit = GetUnit(sensor.SensorType),
            formatted = FormatValue(val, sensor.SensorType)
        };
    }

    public static string GetUnit(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        _ => ""
    };

    public static string FormatValue(float? value, SensorType type)
    {
        var val = CleanFloat(value);
        if (!val.HasValue) return "N/A";
        var unit = GetUnit(type);
        return type switch
        {
            SensorType.Temperature => $"{val.Value:F1}{unit}",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{val.Value:F1}{unit}",
            SensorType.Fan => $"{val.Value:F0} {unit}",
            SensorType.Clock or SensorType.Frequency => $"{val.Value:F0} {unit}",
            SensorType.Power => $"{val.Value:F1} {unit}",
            SensorType.Voltage => $"{val.Value:F3} {unit}",
            SensorType.Current => $"{val.Value:F3} {unit}",
            SensorType.Data => $"{val.Value:F2} {unit}",
            SensorType.SmallData => $"{val.Value:F0} {unit}",
            SensorType.Throughput => FormatThroughput(val.Value),
            _ => $"{val.Value:F2} {unit}"
        };
    }

    private static string FormatThroughput(float bytesPerSec)
    {
        if (bytesPerSec >= 1_073_741_824) return $"{bytesPerSec / 1_073_741_824:F2} GB/s";
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:F2} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:F2} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}
