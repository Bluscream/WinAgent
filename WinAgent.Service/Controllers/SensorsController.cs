using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LibreHardwareMonitor.Hardware;
using WinAgent.Services;
using WinAgent.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

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

    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> GetSensors([FromQuery] SensorsFilterRequest? queryRequest)
    {
        var request = queryRequest ?? new SensorsFilterRequest();

        if (Request.HasJsonContentType())
        {
            try
            {
                var bodyRequest = await Request.ReadFromJsonAsync<SensorsFilterRequest>();
                if (bodyRequest != null)
                {
                    if (!string.IsNullOrEmpty(bodyRequest.HardwareIdentifier)) 
                        request.HardwareIdentifier = bodyRequest.HardwareIdentifier;
                    if (!string.IsNullOrEmpty(bodyRequest.SensorType)) 
                        request.SensorType = bodyRequest.SensorType;
                    if (!string.IsNullOrEmpty(bodyRequest.HardwareType)) 
                        request.HardwareType = bodyRequest.HardwareType;
                }
            }
            catch { }
        }

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
            var tree = WinAgent.Features.HardwareFeatureHelpers.FormatSensorsTree(sensors);

            return Ok(tree);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve sensors: {ex.Message}" });
        }
    }
}
