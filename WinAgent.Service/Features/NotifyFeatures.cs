using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record NotifyRequest(
    string Title = "", 
    string Message = "", 
    bool Toast = false, 
    bool Messagebox = false, 
    bool Ovrtoolkit = false, 
    bool Xsoverlay = false, 
    string Type = "MB_OK", 
    string Icon = "MB_ICONINFORMATION", 
    int TimeoutMs = 5000
);

[Feature(Path = "system/notify", Description = "Send notifications to various targets (Desktop Toast, MessageBox, OVRToolkit, XSOverlay).")]
public class NotifyFeature : BaseFeature<NotifyRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(NotifyRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<NotifyService>();
        var result = await service.NotifyAsync(
            request.Title, 
            request.Message, 
            request.Toast, 
            request.Messagebox, 
            request.Ovrtoolkit, 
            request.Xsoverlay, 
            request.Type, 
            request.Icon, 
            request.TimeoutMs
        );
        return FeatureResult.FromText(result);
    }
}
