using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinAgent.Services;
using System.Text.Json;

namespace WinAgent.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class DeviceController : ControllerBase
{
    private readonly DeviceService _deviceService;

    public DeviceController(DeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    [AcceptVerbs("GET", "POST"), Route("device-list")]
    public async Task<IActionResult> ListDevices([FromQuery] string[]? categories)
    {
        var finalCategories = new List<string>();
        if (categories != null && categories.Length > 0)
        {
            finalCategories.AddRange(categories);
        }

        if (Request.HasJsonContentType())
        {
            try
            {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("categories", out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.EnumerateArray())
                        {
                            var val = item.GetString();
                            if (val != null) finalCategories.Add(val);
                        }
                    }
                    else if (prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (val != null) finalCategories.Add(val);
                    }
                }
            }
            catch { }
        }

        var result = _deviceService.ListDevices(finalCategories.Count > 0 ? finalCategories.ToArray() : null);
        return Ok(result);
    }

    [AcceptVerbs("GET", "POST"), Route("device-enable")]
    public async Task<IActionResult> EnableDevices([FromQuery] string? pattern)
    {
        var finalPattern = await GetPattern(pattern);
        if (string.IsNullOrEmpty(finalPattern)) return BadRequest("Pattern is required.");
        var result = await _deviceService.ToggleDevices(new[] { finalPattern }, null);
        return Ok(result);
    }

    [AcceptVerbs("GET", "POST"), Route("device-disable")]
    public async Task<IActionResult> DisableDevices([FromQuery] string? pattern)
    {
        var finalPattern = await GetPattern(pattern);
        if (string.IsNullOrEmpty(finalPattern)) return BadRequest("Pattern is required.");
        var result = await _deviceService.ToggleDevices(null, new[] { finalPattern });
        return Ok(result);
    }

    [AcceptVerbs("GET", "POST"), Route("device-restart")]
    public async Task<IActionResult> RestartDevices([FromQuery] string? pattern)
    {
        var finalPattern = await GetPattern(pattern);
        if (string.IsNullOrEmpty(finalPattern)) return BadRequest("Pattern is required.");
        var result = await _deviceService.ToggleDevices(new[] { finalPattern }, new[] { finalPattern });
        return Ok(result);
    }

    private async Task<string?> GetPattern(string? queryPattern)
    {
        if (!string.IsNullOrEmpty(queryPattern)) return queryPattern;
        if (Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("pattern", out var prop)) return prop.GetString();
            } catch { }
        }
        return null;
    }
}
