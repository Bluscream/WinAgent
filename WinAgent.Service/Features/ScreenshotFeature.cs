using System;
using System.Threading.Tasks;
using WinAgent.Common.Features;
using WinAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace WinAgent.Features;

public record ScreenshotRequest(string Desktop = "Default", int Quality = 75, string Display = "all", string Format = "png", bool Base64 = false);

[Feature(Path = "capture/screenshot", Description = "Capture a screenshot (JPEG or transparent PNG). Returns a base64-encoded image string or raw image bytes depending on the Base64 argument.")]
public class ScreenshotFeature : BaseFeature<ScreenshotRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ScreenshotRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<ScreenshotService>();
        var bytes = await service.CaptureScreenshot(request.Desktop, request.Quality, request.Display, request.Format);
        
        if (bytes == null || bytes.Length == 0)
        {
            throw new Exception("Screenshot capture returned empty or null bytes.");
        }
        
        bool usePng = string.Equals(request.Format, "png", StringComparison.OrdinalIgnoreCase);
        string mimeType = usePng ? "image/png" : "image/jpeg";
        string ext = usePng ? "png" : "jpg";
        
        if (request.Base64)
        {
            return FeatureResult.FromJson(new { Base64Image = Convert.ToBase64String(bytes), MimeType = mimeType });
        }
        
        return FeatureResult.FromFile(bytes, mimeType, $"screenshot.{ext}");
    }
}

public record ListScreensRequest();

[Feature(Path = "capture/list_screens", Description = "List all available physical screens/monitors.")]
public class ListScreensFeature : BaseFeature<ListScreensRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ListScreensRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var service = services.GetRequiredService<ScreenshotService>();
        var screens = await service.ListScreens();
        return FeatureResult.FromJson(screens);
    }
}
