using LibreHardwareMonitor.Hardware;

namespace WinAgent.Services;

/// <summary>
/// Wraps LibreHardwareMonitor's Computer object, providing thread-safe access
/// to hardware sensors. Requires administrator privileges.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly object _lock = new();
    private bool _isOpen;

    public bool IsOpen => _isOpen;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true
        };
        try
        {
            _computer.Open();
            _computer.Accept(_visitor);
            _isOpen = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WARNING: Failed to auto-initialize HardwareMonitorService: {ex.Message}");
        }
    }

    public void Open()
    {
        lock (_lock)
        {
            if (!_isOpen)
            {
                try
                {
                    _computer.Open();
                    _computer.Accept(_visitor);
                    _isOpen = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Failed to open HardwareMonitorService: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Refreshes all sensor values. Call before reading data.
    /// </summary>
    public void Update()
    {
        lock (_lock)
        {
            _computer.Accept(_visitor);
        }
    }

    /// <summary>
    /// Returns the full hardware tree.
    /// </summary>
    public IList<IHardware> GetHardware()
    {
        lock (_lock)
        {
            return _computer.Hardware;
        }
    }

    /// <summary>
    /// Find hardware by identifier string (e.g. "/cpu/0").
    /// </summary>
    public IHardware? FindHardware(string identifier)
    {
        lock (_lock)
        {
            return FindHardwareRecursive(_computer.Hardware, identifier);
        }
    }

    /// <summary>
    /// Find a specific sensor by its identifier string (e.g. "/cpu/0/temperature/0").
    /// </summary>
    public ISensor? FindSensor(string identifier)
    {
        lock (_lock)
        {
            return FindSensorRecursive(_computer.Hardware, identifier);
        }
    }

    /// <summary>
    /// Returns all sensors across all hardware, optionally filtered.
    /// </summary>
    public List<ISensor> GetAllSensors(
        string? hardwareIdentifier = null,
        SensorType? sensorType = null,
        HardwareType? hardwareType = null)
    {
        lock (_lock)
        {
            var result = new List<ISensor>();
            CollectSensors(_computer.Hardware, result, hardwareIdentifier, sensorType, hardwareType);
            return result;
        }
    }

    private static void CollectSensors(
        IList<IHardware> hardwareList,
        List<ISensor> result,
        string? hardwareIdentifier,
        SensorType? sensorType,
        HardwareType? hardwareType)
    {
        foreach (var hw in hardwareList)
        {
            bool hwMatch = true;
            if (hardwareIdentifier != null && hw.Identifier.ToString() != hardwareIdentifier)
                hwMatch = false;
            if (hardwareType.HasValue && hw.HardwareType != hardwareType.Value)
                hwMatch = false;

            if (hwMatch)
            {
                foreach (var sensor in hw.Sensors)
                {
                    if (sensorType.HasValue && sensor.SensorType != sensorType.Value)
                        continue;
                    result.Add(sensor);
                }
            }

            CollectSensors(hw.SubHardware, result, hardwareIdentifier, sensorType, hardwareType);
        }
    }

    private static IHardware? FindHardwareRecursive(IList<IHardware> list, string identifier)
    {
        foreach (var hw in list)
        {
            if (hw.Identifier.ToString().Equals(identifier, StringComparison.OrdinalIgnoreCase))
                return hw;
            var sub = FindHardwareRecursive(hw.SubHardware, identifier);
            if (sub != null)
                return sub;
        }
        return null;
    }

    private static ISensor? FindSensorRecursive(IList<IHardware> list, string identifier)
    {
        foreach (var hw in list)
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.Identifier.ToString().Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    return sensor;
            }
            var sub = FindSensorRecursive(hw.SubHardware, identifier);
            if (sub != null)
                return sub;
        }
        return null;
    }

    public void Dispose()
    {
        _computer.Close();
    }

    private class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
