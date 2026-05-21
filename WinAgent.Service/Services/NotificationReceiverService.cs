using System;
using WinAgent.Utils;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using WinAgent.Models;
using NotificationData = WinAgent.Models.NotificationData;
using Modern_Windows_Message_Box_Generator.CLI;
using System.Drawing;

namespace WinAgent.Services;

public class NotificationReceiverService : IHostedService
{
    private readonly IMqttManager _mqttManager;
    private readonly NotifyService _notifyService;
    private readonly ILogger<NotificationReceiverService> _logger;
    private string _machineName;
    private string _mqttTopic;

    public NotificationReceiverService(IMqttManager mqttManager, NotifyService notifyService, ILogger<NotificationReceiverService> logger)
    {
        _mqttManager = mqttManager;
        _notifyService = notifyService;
        _logger = logger;
        _machineName = Global.SafeMachineName;
        _mqttTopic = $"homeassistant/notify/{_machineName}/command";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttManager.SubscribeAsync(_mqttTopic, HandleMessageAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payloadString = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            _logger.LogInformation("Received MQTT notification payload: {Payload}", payloadString);

            ToastPayload? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<ToastPayload>(payloadString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                payload = new ToastPayload { Message = payloadString, Title = "Notification", Data = new NotificationData() };
            }

            if (payload != null)
            {
                await _notifyService.ShowNotificationAsync(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MQTT notification message");
        }
    }
}
