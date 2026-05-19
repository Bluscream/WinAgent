using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record LoginRequest(string Username = "", string Password = "", string Domain = "", bool KeepCredentials = false, bool WtsConnect = false);

[Feature(Path = "system/login", Description = "Log in a local Windows user from the login screen using username/password.")]
public class LoginFeature : BaseFeature<LoginRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(LoginRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<LogonRegistryService>();
        var result = service.Login(request.Username, request.Password, request.Domain, request.KeepCredentials, request.WtsConnect);
        return Task.FromResult(FeatureResult.FromJson(new { Status = result }));
    }
}

public record TypeLogonRequest(string Text = "", bool Enter = true);

[Feature(Path = "system/type_logon", Description = "Types text into the active logon session.")]
public class TypeLogonFeature : BaseFeature<TypeLogonRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(TypeLogonRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<LogonRegistryService>();
        var result = await service.TypeLogon(request.Text, request.Enter);
        return FeatureResult.FromJson(new { Status = result });
    }
}

public record ClearCredentialsRequest();

[Feature(Path = "system/clear_credentials", Description = "Clears any staged auto-logon credentials from the registry.")]
public class ClearCredentialsFeature : BaseFeature<ClearCredentialsRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ClearCredentialsRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<LogonRegistryService>();
        var result = await service.ClearCredentials();
        return FeatureResult.FromJson(new { Status = result });
    }
}
