using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinAgent.Services;
using WinAgent.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System;
using WinAgent.Utils;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace WinAgent.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class SystemController : ControllerBase
{
    private readonly ShutdownBlockerService _blocker;
    private readonly ForceActionService _forceActionService;
    private readonly IMqttManager _mqtt;
    private readonly ProcessService _processService;
    private readonly ILogger<SystemController> _logger;

    public SystemController(ShutdownBlockerService blocker, ForceActionService forceActionService, IMqttManager mqtt, ProcessService processService, ILogger<SystemController> logger)
    {
        _blocker = blocker;
        _forceActionService = forceActionService;
        _mqtt = mqtt;
        _processService = processService;
        _logger = logger;
    }

    [HttpPost("notify")]
    public async Task<IActionResult> Notify([FromBody] NotifyRequest request)
    {
        if (string.IsNullOrEmpty(request.Message)) return BadRequest("Message is required.");

        try
        {
            var machineName = Global.SafeMachineName;
            
            // Handle multiple notification types based on flags or the 'Type' string
            bool useToast = request.UseToast ?? request.Type.Contains("toast", StringComparison.OrdinalIgnoreCase);
            bool useMessageBox = request.UseMessageBox ?? request.Type.Contains("messagebox", StringComparison.OrdinalIgnoreCase);
            bool useBanner = request.UseBanner ?? request.Type.Contains("banner", StringComparison.OrdinalIgnoreCase);
            bool useXSOverlay = request.UseXSOverlay ?? request.Type.Contains("xsoverlay", StringComparison.OrdinalIgnoreCase);
            bool useOVRToolkit = request.UseOVRToolkit ?? request.Type.Contains("ovrtoolkit", StringComparison.OrdinalIgnoreCase);

            // Default to Toast if nothing specified
            if (!useToast && !useMessageBox && !useBanner && !useXSOverlay && !useOVRToolkit) useToast = true;

            var topic = $"homeassistant/notify/{machineName}/command";
            var payload = JsonSerializer.Serialize(new ToastPayload
            {
                Title = request.Title,
                Message = request.Message,
                Data = request.Data,
                UseMessageBox = useMessageBox,
                UseBanner = useBanner,
                BannerPosition = request.BannerPosition,
                Heading = request.Heading,
                Footer = request.Footer,
                Details = request.Details,
                Checkbox = request.Checkbox,
                MessageBoxType = request.MessageBoxType,
                MessageBoxIcon = request.MessageBoxIcon,
                Timeout = request.Timeout,
                Classic = request.Classic,
                Callback = request.Callback,
                Flash = request.Flash,
                Ding = request.Ding,
                UseXSOverlay = useXSOverlay,
                UseOVRToolkit = useOVRToolkit
            });
            await _mqtt.EnqueueAsync(topic, payload, false);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing notify request");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("start-process")]
    public async Task<IActionResult> StartProcess([FromBody] StartProcessRequest request)
    {
        if (string.IsNullOrEmpty(request.Executable)) return BadRequest("Executable is required.");

        try
        {
            var result = await _processService.StartProcess(
                request.Executable,
                request.Arguments,
                request.WaitForExit,
                request.Timeout,
                asUser: request.AsUser,
                elevated: request.Elevated);

            return Ok(new { status = "success", result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("block-status")]
    public IActionResult GetBlockStatus()
    {
        return Ok(new { enabled = _blocker.IsBlockingEnabled });
    }

    [AcceptVerbs("GET", "POST"), Route("toggle-block")]
    public async Task<IActionResult> ToggleBlock([FromQuery] bool? enabled)
    {
        bool finalEnabled = enabled ?? false;
        
        if (enabled == null && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("enabled", out var prop)) 
                    finalEnabled = prop.ValueKind == JsonValueKind.True;
            } catch { }
        }

        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
        var payload = finalEnabled ? "ON" : "OFF";
        await _mqtt.EnqueueAsync(topic, payload, true);
        
        return Ok(new { enabled = finalEnabled });
    }

    [HttpGet("force-status")]
    public IActionResult GetForceStatus()
    {
        return Ok(new { enabled = _forceActionService.IsForceEnabled });
    }

    [AcceptVerbs("GET", "POST"), Route("toggle-force")]
    public async Task<IActionResult> ToggleForce([FromQuery] bool? enabled)
    {
        bool finalEnabled = enabled ?? false;

        if (enabled == null && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("enabled", out var prop)) 
                    finalEnabled = prop.ValueKind == JsonValueKind.True;
            } catch { }
        }

        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/switch/{machineName}_force_action/set";
        var payload = finalEnabled ? "ON" : "OFF";
        await _mqtt.EnqueueAsync(topic, payload, true);
        
        return Ok(new { enabled = finalEnabled });
    }

    [AcceptVerbs("GET", "POST"), Route("execute")]
    public async Task<IActionResult> ExecuteAction([FromQuery] string? action)
    {
        string? finalAction = action;

        if (string.IsNullOrEmpty(finalAction) && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("action", out var prop))
                {
                    finalAction = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
                }
            } catch { }
        }

        if (string.IsNullOrEmpty(finalAction)) return BadRequest("Action is required.");

        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/action/{machineName}/command";
        await _mqtt.EnqueueAsync(topic, finalAction, true);
        
        return Ok(new { action = finalAction });
    }

    [HttpGet("system/power-schemes")]
    public IActionResult GetPowerSchemes()
    {
        try
        {
            var schemes = PowerHelper.GetPowerSchemes();
            var active = PowerHelper.GetActiveScheme();
            var result = schemes.Select(s => new {
                name = s.Name,
                guid = s.Guid,
                isActive = s.Name.Equals(active, StringComparison.OrdinalIgnoreCase)
            }).ToList();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [AcceptVerbs("GET", "POST"), Route("system/set-power-scheme")]
    public async Task<IActionResult> SetPowerScheme([FromQuery] string? scheme)
    {
        if (string.IsNullOrEmpty(scheme) && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("scheme", out var prop)) scheme = prop.GetString();
            } catch { }
        }

        if (string.IsNullOrEmpty(scheme)) return BadRequest("Scheme is required.");

        bool success;
        if (Guid.TryParse(scheme, out Guid guid))
        {
            success = PowerHelper.SetActiveScheme(guid);
        }
        else
        {
            success = PowerHelper.SetActiveScheme(scheme);
        }

        if (!success) return BadRequest("Failed to set power scheme.");

        var activeName = PowerHelper.GetActiveScheme();
        var uniqueId = _mqtt.UniqueId;
        await _mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/state", activeName, true);

        var icon = PowerHelper.GetPowerProfileIcon(activeName);
        var attr = new { icon = icon };
        await _mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/attributes", JsonSerializer.Serialize(attr), true);

        return Ok(new { success = true, active = activeName });
    }

    [AcceptVerbs("GET", "POST"), Route("state")]
    public async Task<IActionResult> ReportState([FromQuery] string state, [FromQuery] string? attributes = null)
    {
        if (string.IsNullOrEmpty(state)) return BadRequest("State is required.");

        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/select/{machineName}/state";
        await _mqtt.EnqueueAsync(topic, state, true);

        if (!string.IsNullOrEmpty(attributes))
        {
            var attrTopic = $"homeassistant/select/{machineName}/attributes";
            await _mqtt.EnqueueAsync(attrTopic, attributes, true);
        }
        
        return Ok(new { status = "success" });
    }

    [HttpPost("event")]
    public async Task<IActionResult> FireEvent([FromBody] JsonElement payload)
    {
        try
        {
            var rawJson = payload.GetRawText();
            var node = JsonNode.Parse(rawJson);
            
            string? eventText = null;
            string? eventType = null;
            
            if (node is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("event", out var eTextNode) && eTextNode != null)
                {
                    eventText = eTextNode.ToString();
                }
                if (obj.TryGetPropertyValue("event_type", out var eTypeNode) && eTypeNode != null)
                {
                    eventType = eTypeNode.ToString();
                }
            }
            
            if (string.IsNullOrEmpty(eventText))
            {
                eventText = rawJson;
            }
            if (string.IsNullOrEmpty(eventType))
            {
                eventType = "Generic CLI Event";
            }

            var finalPayload = SystemHelper.BuildAndCleanEventPayload(eventText, eventType, node);
            
            bool httpSuccess = false;
            var hassServer = Config.Get("hass-server", "hass_server", "HASS_SERVER");
            var hassToken = Config.Get("hass-token", "hass_token", "HASS_TOKEN");
            
            if (!string.IsNullOrEmpty(hassServer) && !string.IsNullOrEmpty(hassToken))
            {
                try
                {
                    using var httpClient = new System.Net.Http.HttpClient();
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{hassServer.TrimEnd('/')}/api/events/pc");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hassToken);
                    request.Content = new StringContent(finalPayload, System.Text.Encoding.UTF8, "application/json");
                    var response = await httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        httpSuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to post event to HASS API: {Message}", ex.Message);
                }
            }

            if (!httpSuccess)
            {
                var topic = $"homeassistant/sensor/{_mqtt.UniqueId}_event/state";
                await _mqtt.EnqueueAsync(topic, finalPayload, false);
            }

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error firing event");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
