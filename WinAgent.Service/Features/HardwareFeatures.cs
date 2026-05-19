using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public static class HardwareFeatureHelpers
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetHardwareTypeInfo(HardwareMonitorService service, HardwareType type, string key)
    {
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == type)
                result.Add(FormatHardwareDetail(hw));
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

    public static void CollectHardwareInfo(IList<IHardware> hardwareList, List<object> result, HardwareType? filter)
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

    public static object FormatHardwareDetail(IHardware hw)
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

    public static float? CleanFloat(float? val)
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

public record ListHardwareRequest(string? HardwareType = null);

[Feature(Path = "hardware/list", Description = "List all detected hardware components.")]
public class ListHardwareFeature : BaseFeature<ListHardwareRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(ListHardwareRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        HardwareType? filterType = null;
        if (!string.IsNullOrEmpty(request.HardwareType) && Enum.TryParse<HardwareType>(request.HardwareType, true, out var parsed))
            filterType = parsed;

        var hardware = new List<object>();
        HardwareFeatureHelpers.CollectHardwareInfo(service.GetHardware(), hardware, filterType);
        return Task.FromResult(FeatureResult.FromJson(new { hardware }));
    }
}

public record GetSensorsRequest(string? HardwareIdentifier = null, string? SensorType = null, string? HardwareType = null);

[Feature(Path = "hardware/sensors", Description = "Get sensor readings with optional filters.")]
public class GetSensorsFeature : BaseFeature<GetSensorsRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetSensorsRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();

        SensorType? sType = null;
        HardwareType? hwType = null;

        if (!string.IsNullOrEmpty(request.SensorType) && Enum.TryParse<SensorType>(request.SensorType, true, out var st))
            sType = st;
        if (!string.IsNullOrEmpty(request.HardwareType) && Enum.TryParse<HardwareType>(request.HardwareType, true, out var ht))
            hwType = ht;

        var sensors = service.GetAllSensors(request.HardwareIdentifier, sType, hwType);
        var tree = HardwareFeatureHelpers.FormatSensorsTree(sensors);
        return Task.FromResult(FeatureResult.FromJson(tree));
    }
}

public record GetSensorValueRequest(string SensorIdentifier = "");

[Feature(Path = "hardware/sensor_value", Description = "Get the current value of a specific sensor by its identifier.")]
public class GetSensorValueFeature : BaseFeature<GetSensorValueRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetSensorValueRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();

        var sensor = service.FindSensor(request.SensorIdentifier)
            ?? throw new ArgumentException($"Sensor not found: {request.SensorIdentifier}");

        return Task.FromResult(FeatureResult.FromJson(new
        {
            identifier = sensor.Identifier.ToString(),
            name = sensor.Name,
            hardware = sensor.Hardware.Name,
            hardwareType = sensor.Hardware.HardwareType.ToString(),
            sensorType = sensor.SensorType.ToString(),
            value = sensor.Value,
            min = sensor.Min,
            max = sensor.Max,
            unit = HardwareFeatureHelpers.GetUnit(sensor.SensorType),
            formatted = HardwareFeatureHelpers.FormatValue(sensor.Value, sensor.SensorType)
        }));
    }
}

public record GetHardwareDetailRequest(string HardwareIdentifier = "");

[Feature(Path = "hardware/detail", Description = "Get detailed information about a specific hardware component.")]
public class GetHardwareDetailFeature : BaseFeature<GetHardwareDetailRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetHardwareDetailRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();

        var hw = service.FindHardware(request.HardwareIdentifier)
            ?? throw new ArgumentException($"Hardware not found: {request.HardwareIdentifier}");

        return Task.FromResult(FeatureResult.FromJson(HardwareFeatureHelpers.FormatHardwareDetail(hw)));
    }
}

public record GetSystemSummaryRequest();

[Feature(Path = "hardware/system_summary", Description = "Get a high-level summary of the entire system.")]
public class GetSystemSummaryFeature : BaseFeature<GetSystemSummaryRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetSystemSummaryRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var allSensors = service.GetAllSensors();
        var hardware = service.GetHardware();

        var summary = new Dictionary<string, object>();

        var hwList = new List<object>();
        foreach (var hw in hardware)
            hwList.Add(new { name = hw.Name, type = hw.HardwareType.ToString(), identifier = hw.Identifier.ToString() });
        summary["hardware"] = hwList;

        var temps = allSensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
            .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F1}°C" })
            .ToList();
        summary["temperatures"] = temps;

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

        var fans = allSensors
            .Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue)
            .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F0} RPM" })
            .ToList();
        summary["fans"] = fans;

        var power = allSensors
            .Where(s => s.SensorType == SensorType.Power && s.Value.HasValue)
            .Select(s => new { name = $"{s.Hardware.Name} - {s.Name}", value = $"{s.Value:F1} W" })
            .ToList();
        summary["power"] = power;

        var memory = allSensors
            .Where(s => (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Load)
                        && s.Hardware.HardwareType == HardwareType.Memory && s.Value.HasValue)
            .Select(s => new { name = s.Name, value = HardwareFeatureHelpers.FormatValue(s.Value, s.SensorType) })
            .ToList();
        summary["memory"] = memory;

        return Task.FromResult(FeatureResult.FromJson(summary));
    }
}

public record GetCpuInfoRequest();

[Feature(Path = "hardware/cpu_info", Description = "Get detailed CPU information.")]
public class GetCpuInfoFeature : BaseFeature<GetCpuInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetCpuInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == HardwareType.Cpu)
                result.Add(HardwareFeatureHelpers.FormatHardwareDetail(hw));
            foreach (var sub in hw.SubHardware)
            {
                if (sub.HardwareType == HardwareType.Cpu)
                    result.Add(HardwareFeatureHelpers.FormatHardwareDetail(sub));
            }
        }
        return Task.FromResult(FeatureResult.FromJson(new { cpu = result }));
    }
}

public record GetGpuInfoRequest();

[Feature(Path = "hardware/gpu_info", Description = "Get detailed GPU information.")]
public class GetGpuInfoFeature : BaseFeature<GetGpuInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetGpuInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                result.Add(HardwareFeatureHelpers.FormatHardwareDetail(hw));
        }
        return Task.FromResult(FeatureResult.FromJson(new { gpus = result }));
    }
}

public record GetMemoryInfoRequest();

[Feature(Path = "hardware/memory_info", Description = "Get RAM information.")]
public class GetMemoryInfoFeature : BaseFeature<GetMemoryInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetMemoryInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == HardwareType.Memory)
                result.Add(HardwareFeatureHelpers.FormatHardwareDetail(hw));
        }
        return Task.FromResult(FeatureResult.FromJson(new { memory = result }));
    }
}

public record GetStorageInfoRequest();

[Feature(Path = "hardware/storage_info", Description = "Get storage device information.")]
public class GetStorageInfoFeature : BaseFeature<GetStorageInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetStorageInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == HardwareType.Storage)
                result.Add(HardwareFeatureHelpers.FormatHardwareDetail(hw));
        }
        return Task.FromResult(FeatureResult.FromJson(new { storage = result }));
    }
}

public record GetNetworkInfoRequest();

[Feature(Path = "hardware/network_info", Description = "Get network adapter information.")]
public class GetNetworkInfoFeature : BaseFeature<GetNetworkInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetNetworkInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == HardwareType.Network)
                result.Add(HardwareFeatureHelpers.FormatHardwareDetail(hw));
        }
        return Task.FromResult(FeatureResult.FromJson(new { network = result }));
    }
}

public record GetBatteryInfoRequest();

[Feature(Path = "hardware/battery_info", Description = "Get battery information.")]
public class GetBatteryInfoFeature : BaseFeature<GetBatteryInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetBatteryInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var result = new List<object>();
        foreach (var hw in service.GetHardware())
        {
            if (hw.HardwareType == HardwareType.Battery)
                result.Add(HardwareFeatureHelpers.FormatHardwareDetail(hw));
        }
        return Task.FromResult(FeatureResult.FromJson(new { battery = result }));
    }
}

public record GetFanInfoRequest();

[Feature(Path = "hardware/fan_info", Description = "Get all fan speeds and fan controller information.")]
public class GetFanInfoFeature : BaseFeature<GetFanInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetFanInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var allSensors = service.GetAllSensors(sensorType: SensorType.Fan);
        var controlSensors = service.GetAllSensors(sensorType: SensorType.Control);

        var fans = allSensors.Select(s => HardwareFeatureHelpers.FormatSensor(s)).ToList();
        var controls = controlSensors.Select(s => HardwareFeatureHelpers.FormatSensor(s)).ToList();

        return Task.FromResult(FeatureResult.FromJson(new { fans, controls }));
    }
}

public record GetPowerInfoRequest();

[Feature(Path = "hardware/power_info", Description = "Get comprehensive power information: voltages (V), wattages (W), and currents (A).")]
public class GetPowerInfoFeature : BaseFeature<GetPowerInfoRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(GetPowerInfoRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<HardwareMonitorService>();
        service.Update();
        var allSensors = service.GetAllSensors();

        var voltageSensors = allSensors.Where(s => s.SensorType == SensorType.Voltage && s.Value.HasValue).ToList();
        var powerSensors = allSensors.Where(s => s.SensorType == SensorType.Power && s.Value.HasValue).ToList();
        var currentSensors = allSensors.Where(s => s.SensorType == SensorType.Current && s.Value.HasValue).ToList();
        var energySensors = allSensors.Where(s => s.SensorType == SensorType.Energy && s.Value.HasValue).ToList();

        var byHardware = new Dictionary<string, object>();
        var hwNames = voltageSensors.Select(s => s.Hardware.Name)
            .Union(powerSensors.Select(s => s.Hardware.Name))
            .Union(currentSensors.Select(s => s.Hardware.Name))
            .Union(energySensors.Select(s => s.Hardware.Name))
            .Distinct();

        foreach (var hwName in hwNames)
        {
            var hwVoltages = voltageSensors.Where(s => s.Hardware.Name == hwName).Select(s => HardwareFeatureHelpers.FormatSensor(s)).ToList();
            var hwPower = powerSensors.Where(s => s.Hardware.Name == hwName).Select(s => HardwareFeatureHelpers.FormatSensor(s)).ToList();
            var hwCurrents = currentSensors.Where(s => s.Hardware.Name == hwName).Select(s => HardwareFeatureHelpers.FormatSensor(s)).ToList();
            var hwEnergy = energySensors.Where(s => s.Hardware.Name == hwName).Select(s => HardwareFeatureHelpers.FormatSensor(s)).ToList();

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

        return Task.FromResult(FeatureResult.FromJson(new { byHardware, totals }));
    }
}
