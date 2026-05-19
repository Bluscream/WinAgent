using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record StopMcpRequest();

[Feature(Path = "mcp/stop", Description = "Stops the IPC MCP service.")]
public class StopMcpFeature : BaseFeature<StopMcpRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(StopMcpRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<McpService>();
        var result = service.StopMcp();
        return Task.FromResult(FeatureResult.FromText(result));
    }
}

public record RestartMcpRequest();

[Feature(Path = "mcp/restart", Description = "Restarts the IPC MCP service gracefully.")]
public class RestartMcpFeature : BaseFeature<RestartMcpRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(RestartMcpRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<McpService>();
        var result = service.RestartMcp();
        return Task.FromResult(FeatureResult.FromText(result));
    }
}
