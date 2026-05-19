using System;
using WinAgent.Utils;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;

namespace WinAgent.Services;

public class ActionExecutorService : IHostedService
{
    private readonly ProcessService _processService;
    private readonly WindowsService _windowsService;
    private readonly ForceActionService _forceActionService;
    private readonly LogonRegistryService _logonRegistryService;
    private readonly DeviceService _deviceService;
    private readonly IMqttManager _mqttManager;
    private readonly ILogger<ActionExecutorService> _logger;
    private readonly string _mqttTopic;

    public ActionExecutorService(
        ProcessService processService,
        WindowsService windowsService,
        ForceActionService forceActionService,
        LogonRegistryService logonRegistryService,
        DeviceService deviceService,
        IMqttManager mqttManager,
        ILogger<ActionExecutorService> logger)
    {
        _processService = processService;
        _windowsService = windowsService;
        _forceActionService = forceActionService;
        _logonRegistryService = logonRegistryService;
        _deviceService = deviceService;
        _mqttManager = mqttManager;
        _logger = logger;
        
        _mqttTopic = $"homeassistant/action/{Global.SafeMachineName}/command";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttManager.SubscribeAsync($"homeassistant/select/{_mqttManager.UniqueId}_power_profile/set", HandlePowerProfileSetAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandlePowerProfileSetAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var schemeName = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            _logger.LogInformation($"Setting active power profile to: {schemeName}");
            
            if (PowerHelper.SetActiveScheme(schemeName))
            {
                // Update state topic immediately
                await _mqttManager.EnqueueAsync($"homeassistant/select/{_mqttManager.UniqueId}_power_profile/state", schemeName, true);
                
                var icon = PowerHelper.GetPowerProfileIcon(schemeName);
                var attr = new { icon = icon };
                await _mqttManager.EnqueueAsync($"homeassistant/select/{_mqttManager.UniqueId}_power_profile/attributes", JsonSerializer.Serialize(attr), true);
            }
            else
            {
                _logger.LogWarning($"Failed to set power profile: {schemeName}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling power profile set");
        }
    }
}
