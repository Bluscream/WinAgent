using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;
using WinAgent.Models;

namespace WinAgent.Features;

[Feature(Path = "system/notify", Description = "Send notifications to various targets (Desktop Toast, MessageBox, OVRToolkit, XSOverlay, Banner).")]
public class NotifyFeature : BaseFeature<WinAgent.Models.NotifyRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(WinAgent.Models.NotifyRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<NotifyService>();
        var payload = service.MapRequestToPayload(request);
        await service.ShowNotificationAsync(payload);
        return FeatureResult.FromText("Notification triggered internally successfully");
    }
}
