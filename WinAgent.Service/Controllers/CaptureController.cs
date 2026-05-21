using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinAgent.Services;
using Microsoft.AspNetCore.Http;

namespace WinAgent.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class CaptureController : ControllerBase
{
    private readonly ScreenshotService _screenshotService;

    public CaptureController(ScreenshotService screenshotService)
    {
        _screenshotService = screenshotService;
    }

    public class ScreenshotRequest
    {
        public string? Desktop { get; set; }
        public int? Quality { get; set; }
        public string? Display { get; set; }
        public string? Screen { get; set; }
        public string? Format { get; set; }
    }

    public class StreamRequest
    {
        public string? Desktop { get; set; }
        public int? Quality { get; set; }
        public string? Display { get; set; }
        public string? Screen { get; set; }
        public int? Fps { get; set; }
        public string? Format { get; set; }
    }

    [AcceptVerbs("GET", "POST"), Route("screenshot")]
    public async Task<IActionResult> GetScreenshot([FromQuery] ScreenshotRequest? queryRequest)
    {
        var request = queryRequest ?? new ScreenshotRequest();

        if (Request.HasJsonContentType())
        {
            try
            {
                var bodyRequest = await Request.ReadFromJsonAsync<ScreenshotRequest>();
                if (bodyRequest != null)
                {
                    if (bodyRequest.Desktop != null) request.Desktop = bodyRequest.Desktop;
                    if (bodyRequest.Quality != null) request.Quality = bodyRequest.Quality;
                    if (bodyRequest.Display != null) request.Display = bodyRequest.Display;
                    if (bodyRequest.Screen != null) request.Screen = bodyRequest.Screen;
                    if (bodyRequest.Format != null) request.Format = bodyRequest.Format;
                }
            }
            catch { }
        }

        var finalDesktop = request.Desktop ?? "Default";
        var finalQuality = request.Quality ?? 75;
        var finalDisplay = request.Screen ?? request.Display ?? "all";
        var finalFormat = request.Format ?? "png";

        try
        {
            var bytes = await _screenshotService.CaptureScreenshot(finalDesktop, finalQuality, finalDisplay, finalFormat);
            if (bytes != null)
            {
                var mimeType = finalFormat.ToLower().Contains("png") ? "image/png" : "image/jpeg";
                return File(bytes, mimeType);
            }
            return BadRequest(new { error = "Capture failed." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [AcceptVerbs("GET", "POST"), Route("stream")]
    public async Task GetStream([FromQuery] StreamRequest? queryRequest)
    {
        var request = queryRequest ?? new StreamRequest();

        if (Request.HasJsonContentType())
        {
            try
            {
                var bodyRequest = await Request.ReadFromJsonAsync<StreamRequest>();
                if (bodyRequest != null)
                {
                    if (bodyRequest.Desktop != null) request.Desktop = bodyRequest.Desktop;
                    if (bodyRequest.Quality != null) request.Quality = bodyRequest.Quality;
                    if (bodyRequest.Display != null) request.Display = bodyRequest.Display;
                    if (bodyRequest.Screen != null) request.Screen = bodyRequest.Screen;
                    if (bodyRequest.Fps != null) request.Fps = bodyRequest.Fps;
                    if (bodyRequest.Format != null) request.Format = bodyRequest.Format;
                }
            }
            catch { }
        }

        var finalQuality = request.Quality ?? 50;
        var finalDisplay = request.Screen ?? request.Display ?? "all";
        var finalFps = request.Fps ?? 10;

        Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        var ct = HttpContext.RequestAborted;

        try
        {
            await _screenshotService.StartStreamingProcess(finalDisplay, finalQuality, finalFps, Response.Body, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaptureController] Stream error: {ex.Message}");
        }
    }
}
