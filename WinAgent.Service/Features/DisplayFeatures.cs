using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record DisplaysRequest(string Action = "", string? Monitor = null, string? Value = null);

[Feature(Path = "system/displays", Description = "Control monitors using MultiMonitorTool. Possible actions: list, enable, disable, switch, setprimary, setorientation, setscale, setmax, turnoff, turnon, switchoffon, movealltoprimary.")]
public class DisplaysFeature : BaseFeature<DisplaysRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(DisplaysRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<MultiMonitorToolService>();
        var result = request.Action.ToLower() switch
        {
            "list" => await service.GetMonitorsAsync("json"),
            "enable" => await service.EnableAsync(request.Monitor ?? throw new ArgumentException("Monitor is required")),
            "disable" => await service.DisableAsync(request.Monitor ?? throw new ArgumentException("Monitor is required")),
            "switch" => await service.SwitchAsync(request.Monitor ?? throw new ArgumentException("Monitor is required")),
            "setprimary" => await service.SetPrimaryAsync(request.Monitor ?? throw new ArgumentException("Monitor is required")),
            "setorientation" => await service.SetOrientationAsync(request.Monitor ?? throw new ArgumentException("Monitor is required"), int.Parse(request.Value ?? "0")),
            "setscale" => await service.SetScaleAsync(request.Monitor ?? throw new ArgumentException("Monitor is required"), int.Parse(request.Value ?? "100")),
            "setmax" => await service.SetMaxResolutionAsync(request.Monitor ?? throw new ArgumentException("Monitor is required")),
            "turnoff" => await service.TurnOffMonitorsAsync(),
            "turnon" => await service.TurnOnMonitorsAsync(),
            "switchoffon" => await service.SwitchOffOnMonitorsAsync(),
            "movealltoprimary" => await service.MoveAllWindowsToPrimaryAsync(),
            _ => throw new ArgumentException($"Unknown action: {request.Action}")
        };
        return FeatureResult.FromText(result);
    }
}
