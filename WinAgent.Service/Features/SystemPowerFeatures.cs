using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;
using WinAgent.Utils;

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

public record BlockShutdownRequest(object? State = null);

[Feature(Path = "system/block_shutdown", Description = "Get, set, or toggle shutdown blocking status.")]
public class BlockShutdownFeature : BaseFeature<BlockShutdownRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(BlockShutdownRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var blocker = services.GetRequiredService<ShutdownBlockerService>();
        var currentVal = blocker.IsBlockingEnabled;
        
        var parsed = SystemHelper.ParseState(request.State, currentVal);
        if (parsed.HasValue)
        {
            var targetState = parsed.Value;
            if (targetState != currentVal)
            {
                var mqtt = services.GetRequiredService<IMqttManager>();
                var machineName = Global.SafeMachineName;
                var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
                var payload = targetState ? "ON" : "OFF";
                await mqtt.EnqueueAsync(topic, payload, true);
                
                return FeatureResult.FromJson(new { Enabled = targetState });
            }
        }
        
        return FeatureResult.FromJson(new { Enabled = currentVal });
    }
}

public record ForceActionRequest(object? State = null);

[Feature(Path = "system/force_action", Description = "Get, set, or toggle force action status.")]
public class ForceActionFeature : BaseFeature<ForceActionRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ForceActionRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var forceActionService = services.GetRequiredService<ForceActionService>();
        var currentVal = forceActionService.IsForceEnabled;
        
        var parsed = SystemHelper.ParseState(request.State, currentVal);
        if (parsed.HasValue)
        {
            var targetState = parsed.Value;
            if (targetState != currentVal)
            {
                var mqtt = services.GetRequiredService<IMqttManager>();
                var machineName = Global.SafeMachineName;
                var topic = $"homeassistant/switch/{machineName}_force_action/set";
                var payload = targetState ? "ON" : "OFF";
                await mqtt.EnqueueAsync(topic, payload, true);
                
                return FeatureResult.FromJson(new { Enabled = targetState });
            }
        }
        
        return FeatureResult.FromJson(new { Enabled = currentVal });
    }
}

public record ExecuteActionRequest(string Action = "");

[Feature(Path = "system/execute_action", Description = "Execute a specific system power action (shutdown, reboot, lock, logoff, etc.).")]
public class ExecuteActionFeature : BaseFeature<ExecuteActionRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ExecuteActionRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        if (string.IsNullOrEmpty(request.Action))
        {
            throw new ArgumentException("Action is required.");
        }
        
        var mqtt = services.GetRequiredService<IMqttManager>();
        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/action/{machineName}/command";
        await mqtt.EnqueueAsync(topic, request.Action, true);
        
        return FeatureResult.FromJson(new { Action = request.Action });
    }
}

public record PowerSchemesRequest();

[Feature(Path = "system/power_schemes", Description = "Get all configured system power schemes/profiles.")]
public class PowerSchemesFeature : BaseFeature<PowerSchemesRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(PowerSchemesRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var schemes = PowerHelper.GetPowerSchemes();
        var active = PowerHelper.GetActiveScheme();
        var result = schemes.Select(s => new {
            name = s.Name,
            guid = s.Guid,
            isActive = s.Name.Equals(active, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        return Task.FromResult(FeatureResult.FromJson(result));
    }
}

public record SetPowerSchemeRequest(string Scheme = "");

[Feature(Path = "system/set_power_scheme", Description = "Set the active system power scheme/profile.")]
public class SetPowerSchemeFeature : BaseFeature<SetPowerSchemeRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(SetPowerSchemeRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        if (string.IsNullOrEmpty(request.Scheme))
        {
            throw new ArgumentException("Scheme is required.");
        }
        
        bool success;
        if (Guid.TryParse(request.Scheme, out Guid guid))
        {
            success = PowerHelper.SetActiveScheme(guid);
        }
        else
        {
            success = PowerHelper.SetActiveScheme(request.Scheme);
        }
        
        if (!success)
        {
            throw new ArgumentException("Failed to set power scheme.");
        }
        
        var activeName = PowerHelper.GetActiveScheme();
        var mqtt = services.GetRequiredService<IMqttManager>();
        var uniqueId = mqtt.UniqueId;
        await mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/state", activeName, true);
        
        var icon = PowerHelper.GetPowerProfileIcon(activeName);
        var attr = new { icon = icon };
        await mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/attributes", JsonSerializer.Serialize(attr), true);
        
        return FeatureResult.FromJson(new { Success = true, Active = activeName });
    }
}
