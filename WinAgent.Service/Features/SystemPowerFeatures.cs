using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record ShutdownRequest(bool Force = false, int Timeout = 0, string? Message = null);

[Feature(Path = "system/shutdown", Description = "Shuts down the system.")]
[MqttButton("shutdown")]
public class ShutdownFeature : BaseFeature<ShutdownRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(ShutdownRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<WindowsService>();
        var result = service.Shutdown(reboot: false, force: request.Force, timeout: request.Timeout, message: request.Message);
        return Task.FromResult(FeatureResult.FromJson(new { Status = result }));
    }
}

public record RebootRequest(bool Force = false, int Timeout = 0, string? Message = null);

[Feature(Path = "system/reboot", Description = "Reboots the system.")]
[MqttButton("reboot")]
public class RebootFeature : BaseFeature<RebootRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(RebootRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<WindowsService>();
        var result = service.Shutdown(reboot: true, force: request.Force, timeout: request.Timeout, message: request.Message);
        return Task.FromResult(FeatureResult.FromJson(new { Status = result }));
    }
}

public record LockRequest();

[Feature(Path = "system/lock", Description = "Locks the current Windows workstation.")]
[MqttButton("lock")]
public class LockFeature : BaseFeature<LockRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(LockRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<WindowsService>();
        var result = await service.Lock();
        return FeatureResult.FromJson(new { Status = result });
    }
}

public record LogoutRequest(bool AllUsers = false, string? Message = null, int Timeout = 0);

[Feature(Path = "system/logout", Description = "Logs out the current user or all users.")]
[MqttButton("logout")]
public class LogoutFeature : BaseFeature<LogoutRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(LogoutRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<WindowsService>();
        var result = service.Logout(request.AllUsers, request.Message, request.Timeout);
        return Task.FromResult(FeatureResult.FromJson(new { Status = result }));
    }
}
