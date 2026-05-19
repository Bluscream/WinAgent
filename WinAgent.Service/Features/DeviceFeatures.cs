using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record DevicesRequest(string[]? Enable = null, string[]? Disable = null, string[]? Categories = null);

[Feature(Path = "system/devices", Description = "Control PnP devices (enable, disable, restart).")]
public class DevicesFeature : BaseFeature<DevicesRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(DevicesRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<DeviceService>();
        if ((request.Enable != null && request.Enable.Length > 0) || (request.Disable != null && request.Disable.Length > 0))
        {
            await service.ToggleDevices(request.Enable, request.Disable);
        }
        var result = service.ListDevices(request.Categories);
        return FeatureResult.FromText(result);
    }
}
