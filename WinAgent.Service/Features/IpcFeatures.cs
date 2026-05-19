using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record NamedPipeRequest(string PipeName = "", string? Message = null, int Timeout = 30000, int CheckInterval = 500, string? Pattern = null);

[Feature(Path = "ipc/named_pipe", Description = "Interact with a named pipe. If pattern is set to '.*', waits for a response. Otherwise, just waits for the pipe to exist. Optionally sends a message first.")]
public class NamedPipeFeature : BaseFeature<NamedPipeRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(NamedPipeRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<NamedPipeService>();
        var result = await service.NamedPipe(request.PipeName, request.Message, request.Timeout, request.CheckInterval, request.Pattern);
        return FeatureResult.FromText(result);
    }
}

public record MappedFileRequest(string MapName = "", string? Message = null, long Offset = 0, int Length = 4096);

[Feature(Path = "ipc/mapped_file", Description = "Read from or write to a memory-mapped file. If message is provided, writes to the file. Otherwise, reads from it.")]
public class MappedFileFeature : BaseFeature<MappedFileRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(MappedFileRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<MemoryMappedFileService>();
        var result = service.MappedFile(request.MapName, request.Message, request.Offset, request.Length);
        return Task.FromResult(FeatureResult.FromText(result));
    }
}

public record RegistryRequest(string KeyPath = "", string? ValueName = null, string? Value = null, string ValueType = "String", string Hive = "HKEY_CURRENT_USER");

[Feature(Path = "ipc/registry", Description = "Read from or write to a specific Windows registry value.")]
public class RegistryFeature : BaseFeature<RegistryRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(RegistryRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<RegistryService>();
        string result;
        if (!string.IsNullOrEmpty(request.Value) && !string.IsNullOrEmpty(request.ValueName))
        {
            result = service.WriteRegistry(request.KeyPath, request.ValueName, request.Value, request.ValueType, request.Hive);
        }
        else
        {
            result = service.ReadRegistry(request.KeyPath, request.ValueName, request.Hive);
        }
        return Task.FromResult(FeatureResult.FromText(result));
    }
}

public record ComRequest(string? ProgId = null, string? Method = null, string? Parameters = null, string? Clsid = null);

[Feature(Path = "ipc/com", Description = "Query a specific COM object's registration or call its methods.")]
public class ComFeature : BaseFeature<ComRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(ComRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<ComService>();
        string result;
        if (!string.IsNullOrEmpty(request.Method) && !string.IsNullOrEmpty(request.ProgId))
        {
            Dictionary<string, object>? paramsDict = null;
            if (!string.IsNullOrEmpty(request.Parameters))
            {
                paramsDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(request.Parameters);
            }
            result = service.SendComMessage(request.ProgId, request.Method, paramsDict);
        }
        else
        {
            result = service.QueryComObject(request.ProgId, request.Clsid);
        }
        return Task.FromResult(FeatureResult.FromText(result));
    }
}

public record SearchRegistryRequest(string Query = "", string? Path = null, bool SearchKeys = true, bool SearchValues = true, bool SearchData = true, string Hive = "HKEY_CURRENT_USER");

[Feature(Path = "ipc/search_registry", Description = "Search the Windows registry using glob patterns.")]
public class SearchRegistryFeature : BaseFeature<SearchRegistryRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(SearchRegistryRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<RegistryService>();
        var result = service.SearchRegistry(request.Query, request.Path, request.SearchKeys, request.SearchValues, request.SearchData, request.Hive);
        return Task.FromResult(FeatureResult.FromText(result));
    }
}
