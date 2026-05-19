using System;
using System.Threading.Tasks;

namespace WinAgent.Common.Features;

public interface IBaseFeature<TRequest, TResponse>
{
    Task<TResponse> ExecuteAsync(TRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context);
}

public abstract class BaseFeature<TRequest, TResponse> : IBaseFeature<TRequest, TResponse>
{
    public abstract Task<TResponse> ExecuteAsync(TRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context);
}

/// <summary>
/// A marker interface so we can easily collect all features in the DI container without knowing TRequest/TResponse.
/// </summary>
public interface IFeatureDefinition
{
}
