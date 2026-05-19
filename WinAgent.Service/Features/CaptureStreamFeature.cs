using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WinAgent.Common.Features;
using WinAgent.Services;

namespace WinAgent.Features;

public record ScreenshotStreamRequest(
    string Desktop = "Default", 
    int Quality = 50, 
    string Display = "all", 
    string? Screen = null, 
    int Fps = 10, 
    string Format = "mjpeg"
);

[Feature(Path = "capture/stream", Description = "Stream screenshots continuously (MJPEG streaming). ONLY supported over HTTP.")]
public class CaptureStreamFeature : BaseFeature<ScreenshotStreamRequest, FeatureResult>, IFeatureDefinition
{
    public override async Task<FeatureResult> ExecuteAsync(ScreenshotStreamRequest request, IServiceProvider services, WinAgent.Common.Features.ExecutionContext context)
    {
        var httpContextAccessor = services.GetService<IHttpContextAccessor>();
        var httpContext = httpContextAccessor?.HttpContext;

        if (httpContext == null)
        {
            return FeatureResult.FromText("Continuous streaming is only supported over HTTP (API/Web).");
        }

        var service = services.GetRequiredService<ScreenshotService>();
        var display = request.Screen ?? request.Display;

        httpContext.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        var ct = httpContext.RequestAborted;

        try
        {
            await service.StartStreamingProcess(display, request.Quality, request.Fps, httpContext.Response.Body, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaptureStreamFeature] Stream error: {ex.Message}");
        }

        // Return empty feature result since response has already been written directly to body
        return new FeatureResult();
    }
}
