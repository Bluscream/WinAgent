using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LibreHardwareMonitor.Hardware;
using WinAgent.Services;
using WinAgent.Tools;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WinAgent.Controllers;

[ApiController]
[Route("api/sensors")]
[Authorize]
public class SensorsController : ControllerBase
{
    private readonly HardwareMonitorService _hardwareMonitor;

    public SensorsController(HardwareMonitorService hardwareMonitor)
    {
        _hardwareMonitor = hardwareMonitor;
    }

    public class SensorsFilterRequest
    {
        public string? HardwareIdentifier { get; set; }
        public string? SensorType { get; set; }
        public string? HardwareType { get; set; }
    }

    [HttpGet]
    public IActionResult GetSensors(
        [FromQuery] string? hardwareIdentifier = null,
        [FromQuery] string? sensorType = null,
        [FromQuery] string? hardwareType = null)
    {
        try
        {
            _hardwareMonitor.Update();

            SensorType? sType = null;
            HardwareType? hwType = null;

            if (!string.IsNullOrEmpty(sensorType) && Enum.TryParse<SensorType>(sensorType, true, out var st))
                sType = st;
            if (!string.IsNullOrEmpty(hardwareType) && Enum.TryParse<HardwareType>(hardwareType, true, out var ht))
                hwType = ht;

            var sensors = _hardwareMonitor.GetAllSensors(hardwareIdentifier, sType, hwType);
            var tree = HardwareTools.FormatSensorsTree(sensors);

            return Ok(tree);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve sensors: {ex.Message}" });
        }
    }

    [HttpPost]
    public IActionResult GetSensorsPost([FromBody] SensorsFilterRequest request)
    {
        try
        {
            _hardwareMonitor.Update();

            SensorType? sType = null;
            HardwareType? hwType = null;

            if (!string.IsNullOrEmpty(request.SensorType) && Enum.TryParse<SensorType>(request.SensorType, true, out var st))
                sType = st;
            if (!string.IsNullOrEmpty(request.HardwareType) && Enum.TryParse<HardwareType>(request.HardwareType, true, out var ht))
                hwType = ht;

            var sensors = _hardwareMonitor.GetAllSensors(request.HardwareIdentifier, sType, hwType);
            var tree = HardwareTools.FormatSensorsTree(sensors);

            return Ok(tree);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve sensors: {ex.Message}" });
        }
    }
}
