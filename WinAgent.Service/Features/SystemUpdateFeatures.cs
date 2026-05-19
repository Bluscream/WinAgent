using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record UpdateRequest(bool Install = false, bool RebootIfNeeded = false);

[Feature(Path = "system/update", Description = "Restarts the Windows Update service and triggers a search for updates.")]
public class UpdateFeature : BaseFeature<UpdateRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(UpdateRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<UpdateService>();
        var result = service.Update(request.Install, request.RebootIfNeeded);
        return Task.FromResult(FeatureResult.FromText(result));
    }
}
