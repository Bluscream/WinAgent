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
    private readonly NotifyService _notifyService;
    private readonly ILogger<SystemController> _logger;

    public SystemController(ShutdownBlockerService blocker, ForceActionService forceActionService, IMqttManager mqtt, ProcessService processService, NotifyService notifyService, ILogger<SystemController> logger)
    {
        _blocker = blocker;
        _forceActionService = forceActionService;
        _mqtt = mqtt;
        _processService = processService;
        _notifyService = notifyService;
        _logger = logger;
    }

    [AcceptVerbs("GET", "POST"), Route("notify")]
    public async Task<IActionResult> Notify([FromQuery] NotifyRequest? queryRequest)
    {
        var request = queryRequest ?? new NotifyRequest();

        if (Request.HasJsonContentType())
        {
            try
            {
                var bodyRequest = await Request.ReadFromJsonAsync<NotifyRequest>();
                if (bodyRequest != null)
                {
                    if (!string.IsNullOrEmpty(bodyRequest.Message)) request.Message = bodyRequest.Message;
                    if (!string.IsNullOrEmpty(bodyRequest.Title) && bodyRequest.Title != "Notification") request.Title = bodyRequest.Title;
                    if (!string.IsNullOrEmpty(bodyRequest.Heading)) request.Heading = bodyRequest.Heading;
                    if (!string.IsNullOrEmpty(bodyRequest.Footer)) request.Footer = bodyRequest.Footer;
                    if (!string.IsNullOrEmpty(bodyRequest.Details)) request.Details = bodyRequest.Details;
                    if (!string.IsNullOrEmpty(bodyRequest.Checkbox)) request.Checkbox = bodyRequest.Checkbox;
                    if (!string.IsNullOrEmpty(bodyRequest.Type) && bodyRequest.Type != "toast") request.Type = bodyRequest.Type;
                    if (!string.IsNullOrEmpty(bodyRequest.MessageBoxType) && bodyRequest.MessageBoxType != "ok") request.MessageBoxType = bodyRequest.MessageBoxType;
                    if (!string.IsNullOrEmpty(bodyRequest.MessageBoxIcon) && bodyRequest.MessageBoxIcon != "info") request.MessageBoxIcon = bodyRequest.MessageBoxIcon;
                    if (bodyRequest.Timeout != 0) request.Timeout = bodyRequest.Timeout;
                    if (bodyRequest.Classic) request.Classic = bodyRequest.Classic;
                    if (!string.IsNullOrEmpty(bodyRequest.Callback)) request.Callback = bodyRequest.Callback;
                    if (bodyRequest.Flash) request.Flash = bodyRequest.Flash;
                    if (bodyRequest.Ding) request.Ding = bodyRequest.Ding;
                    if (bodyRequest.UseToast != null) request.UseToast = bodyRequest.UseToast;
                    if (bodyRequest.UseMessageBox != null) request.UseMessageBox = bodyRequest.UseMessageBox;
                    if (bodyRequest.UseBanner != null) request.UseBanner = bodyRequest.UseBanner;
                    if (bodyRequest.UseXSOverlay != null) request.UseXSOverlay = bodyRequest.UseXSOverlay;
                    if (bodyRequest.UseOVRToolkit != null) request.UseOVRToolkit = bodyRequest.UseOVRToolkit;
                    if (!string.IsNullOrEmpty(bodyRequest.BannerPosition)) request.BannerPosition = bodyRequest.BannerPosition;
                    if (!string.IsNullOrEmpty(bodyRequest.Image)) request.Image = bodyRequest.Image;
                    if (bodyRequest.Data != null) request.Data = bodyRequest.Data;
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(request.Message)) return BadRequest("Message is required.");

        try
        {
            var payload = _notifyService.MapRequestToPayload(request);
            await _notifyService.ShowNotificationAsync(payload);

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing notify request");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [AcceptVerbs("GET", "POST"), Route("start-process")]
    public async Task<IActionResult> StartProcess([FromQuery] StartProcessRequest? queryRequest)
    {
        var request = queryRequest ?? new StartProcessRequest();

        if (Request.HasJsonContentType())
        {
            try
            {
                var bodyRequest = await Request.ReadFromJsonAsync<StartProcessRequest>();
                if (bodyRequest != null)
                {
                    if (!string.IsNullOrEmpty(bodyRequest.Executable)) request.Executable = bodyRequest.Executable;
                    if (!string.IsNullOrEmpty(bodyRequest.Arguments)) request.Arguments = bodyRequest.Arguments;
                    if (!string.IsNullOrEmpty(bodyRequest.AsUser)) request.AsUser = bodyRequest.AsUser;
                    if (bodyRequest.Elevated) request.Elevated = bodyRequest.Elevated;
                    if (bodyRequest.WaitForExit) request.WaitForExit = bodyRequest.WaitForExit;
                    if (bodyRequest.Timeout != 30000) request.Timeout = bodyRequest.Timeout;
                }
            }
            catch { }
        }

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

    [AcceptVerbs("GET", "POST"), Route("block-shutdown"), Route("system/block-shutdown")]
    public async Task<IActionResult> BlockShutdown([FromQuery] string? state)
    {
        object? finalState = state;

        if (string.IsNullOrEmpty(state) && Request.HasJsonContentType())
        {
            try
            {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("state", out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.True) finalState = true;
                    else if (prop.ValueKind == JsonValueKind.False) finalState = false;
                    else if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val)) finalState = val;
                    else if (prop.ValueKind == JsonValueKind.String) finalState = prop.GetString();
                }
                else if (body.TryGetProperty("State", out var propCamel))
                {
                    if (propCamel.ValueKind == JsonValueKind.True) finalState = true;
                    else if (propCamel.ValueKind == JsonValueKind.False) finalState = false;
                    else if (propCamel.ValueKind == JsonValueKind.Number && propCamel.TryGetInt32(out int val)) finalState = val;
                    else if (propCamel.ValueKind == JsonValueKind.String) finalState = propCamel.GetString();
                }
            }
            catch { }
        }

        var currentVal = _blocker.IsBlockingEnabled;
        var parsed = SystemHelper.ParseState(finalState, currentVal);

        if (parsed.HasValue)
        {
            var targetState = parsed.Value;
            if (targetState != currentVal)
            {
                var machineName = Global.SafeMachineName;
                var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
                var payload = targetState ? "ON" : "OFF";
                await _mqtt.EnqueueAsync(topic, payload, true);
                currentVal = targetState;
            }
        }

        return Ok(new { enabled = currentVal });
    }

    [AcceptVerbs("GET", "POST"), Route("force-action"), Route("system/force-action")]
    public async Task<IActionResult> ForceAction([FromQuery] string? state)
    {
        object? finalState = state;

        if (string.IsNullOrEmpty(state) && Request.HasJsonContentType())
        {
            try
            {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("state", out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.True) finalState = true;
                    else if (prop.ValueKind == JsonValueKind.False) finalState = false;
                    else if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val)) finalState = val;
                    else if (prop.ValueKind == JsonValueKind.String) finalState = prop.GetString();
                }
                else if (body.TryGetProperty("State", out var propCamel))
                {
                    if (propCamel.ValueKind == JsonValueKind.True) finalState = true;
                    else if (propCamel.ValueKind == JsonValueKind.False) finalState = false;
                    else if (propCamel.ValueKind == JsonValueKind.Number && propCamel.TryGetInt32(out int val)) finalState = val;
                    else if (propCamel.ValueKind == JsonValueKind.String) finalState = propCamel.GetString();
                }
            }
            catch { }
        }

        var currentVal = _forceActionService.IsForceEnabled;
        var parsed = SystemHelper.ParseState(finalState, currentVal);

        if (parsed.HasValue)
        {
            var targetState = parsed.Value;
            if (targetState != currentVal)
            {
                var machineName = Global.SafeMachineName;
                var topic = $"homeassistant/switch/{machineName}_force_action/set";
                var payload = targetState ? "ON" : "OFF";
                await _mqtt.EnqueueAsync(topic, payload, true);
                currentVal = targetState;
            }
        }

        return Ok(new { enabled = currentVal });
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

    [AcceptVerbs("GET", "POST"), Route("system/power-schemes")]
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
    public async Task<IActionResult> ReportState([FromQuery] string? state, [FromQuery] string? attributes = null)
    {
        string? finalState = state;
        string? finalAttributes = attributes;

        if (string.IsNullOrEmpty(finalState) && Request.HasJsonContentType())
        {
            try
            {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("state", out var stateProp))
                {
                    finalState = stateProp.ValueKind == JsonValueKind.String ? stateProp.GetString() : stateProp.GetRawText();
                }
                if (body.TryGetProperty("attributes", out var attrProp))
                {
                    finalAttributes = attrProp.ValueKind == JsonValueKind.String ? attrProp.GetString() : attrProp.GetRawText();
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(finalState)) return BadRequest("State is required.");

        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/select/{machineName}/state";
        await _mqtt.EnqueueAsync(topic, finalState, true);

        if (!string.IsNullOrEmpty(finalAttributes))
        {
            var attrTopic = $"homeassistant/select/{machineName}/attributes";
            await _mqtt.EnqueueAsync(attrTopic, finalAttributes, true);
        }
        
        return Ok(new { status = "success" });
    }

    [AcceptVerbs("GET", "POST"), Route("event")]
    public async Task<IActionResult> FireEvent([FromQuery] string? @event, [FromQuery] string? event_type = null)
    {
        try
        {
            string? eventText = @event;
            string? eventType = event_type;
            JsonNode? node = null;

            if (Request.HasJsonContentType())
            {
                try
                {
                    var body = await Request.ReadFromJsonAsync<JsonElement>();
                    var rawJson = body.GetRawText();
                    node = JsonNode.Parse(rawJson);

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
                }
                catch { }
            }

            if (string.IsNullOrEmpty(eventText))
            {
                return BadRequest("Event text or body is required.");
            }

            if (node == null)
            {
                node = new JsonObject
                {
                    ["event"] = eventText,
                    ["event_type"] = eventType ?? "Generic CLI Event"
                };
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
