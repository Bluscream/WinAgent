using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record AudioRequest(string[]? Enable = null, string[]? Disable = null, Dictionary<string, int>? SetVolumes = null);

[Feature(Path = "system/audio", Description = "Control audio input/output devices (enable, disable, volume).")]
public class AudioFeature : BaseFeature<AudioRequest, FeatureResult>, IFeatureDefinition
{
    public override Task<FeatureResult> ExecuteAsync(AudioRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<AudioService>();
        
        if (request.SetVolumes != null)
        {
            foreach (var kvp in request.SetVolumes)
            {
                service.SetAudioVolume(kvp.Key, kvp.Value);
            }
        }

        if (request.Enable != null)
        {
            foreach (var id in request.Enable) service.ToggleAudioDevice(id, true);
        }
        if (request.Disable != null)
        {
            foreach (var id in request.Disable) service.ToggleAudioDevice(id, false);
        }

        var result = service.ListAudioDevices();
        return Task.FromResult(FeatureResult.FromText(result));
    }
}
