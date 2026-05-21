using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using WinAgent.Utils;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using WinAgent.Utils.Capture;

namespace WinAgent.Services
{
    public class ScreenshotService
    {
        private readonly ProcessService _processService;
        private readonly MultiMonitorToolService _monitorService;
        private readonly Dictionary<string, ICaptureBackend> _backends = new();

        public ScreenshotService(ProcessService processService, MultiMonitorToolService monitorService)
        {
            _processService = processService;
            _monitorService = monitorService;
        }

        public async Task<byte[]?> CaptureScreenshot(string desktop = "Default", int quality = 75, string display = "all", string format = "png")
        {
            List<string> errors = new List<string>();
            bool usePng = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);

            // Resolve friendly name to device name or index if needed
            string resolvedDisplay = await _monitorService.ResolveMonitorName(display);
            if (!string.Equals(resolvedDisplay, display))
            {
                Console.WriteLine($"[ScreenshotService] Resolved friendly name '{display}' to '{resolvedDisplay}'");
            }

            // 1. Prefer high-performance authed Named Pipe IPC to the Tray App running in user Session 1+
            if (IpcServerService.IsTrayConnected)
            {
                try
                {
                    Console.WriteLine("[ScreenshotService] Tray is connected. Requesting screenshot over IPC...");
                    var reqPayload = JsonSerializer.Serialize(new { Display = resolvedDisplay, Quality = quality, Format = format });
                    var ipcResponse = await IpcServerService.SendRequestToTrayAsync("tray/screenshot", reqPayload, 15000);
                    if (ipcResponse != null && ipcResponse.Success == true && !string.IsNullOrEmpty(ipcResponse.Payload))
                    {
                        var bytes = Convert.FromBase64String(ipcResponse.Payload);
                        if (bytes != null && bytes.Length > 100)
                        {
                            Console.WriteLine($"[ScreenshotService] SUCCESS (Tray IPC): {bytes.Length} bytes");
                            return bytes;
                        }
                        else errors.Add("Tray IPC screenshot returned invalid/empty bytes.");
                    }
                    else errors.Add(ipcResponse?.Error ?? "Tray IPC returned failure response.");
                }
                catch (Exception ex)
                {
                    errors.Add($"Tray IPC Capture Exception: {ex.Message}");
                }
            }
            else
            {
                errors.Add("Tray application is not connected.");
            }

            // 2. Direct Fallback: Capture directly in the Service Process (no process spawning!)
            try
            {
                Console.WriteLine($"[ScreenshotService] Falling back to Direct Capture (Display: {resolvedDisplay})");
                
                if (usePng || resolvedDisplay.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    using var gdi = new GdiCaptureBackend();
                    gdi.Initialize(resolvedDisplay);
                    var bytes = await gdi.CaptureFrame(quality);
                    if (bytes != null) return bytes;
                }
                else
                {
                    ICaptureBackend backend;
                    if (!_backends.TryGetValue(resolvedDisplay, out backend!) || backend is GdiCaptureBackend)
                    {
                        backend = new DxgiCaptureBackend();
                        backend.Initialize(resolvedDisplay);
                        _backends[resolvedDisplay] = backend;
                    }

                    var bytes = await backend.CaptureFrame(quality);
                    if (bytes == null)
                    {
                        // Fallback to GDI for this display
                        backend = new GdiCaptureBackend();
                        backend.Initialize(resolvedDisplay);
                        _backends[resolvedDisplay] = backend;
                        bytes = await backend.CaptureFrame(quality);
                    }
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Direct capture exception: {ex.Message}");
            }

            Console.Error.WriteLine("ERROR: All screenshot methods failed.\n" + string.Join("\n", errors));
            return null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern bool EnumDesktops(IntPtr hwinsta, EnumDesktopsDelegate lpEnumCallback, IntPtr lParam);

        private delegate bool EnumDesktopsDelegate(string lpszDesktop, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseWindowStation(IntPtr hWinSta);

        private const uint MAXIMUM_ALLOWED = 0x02000000;

        public async Task<List<object>> ListScreens()
        {
            try
            {
                var json = await _monitorService.GetMonitorsAsync("json");
                if (!string.IsNullOrEmpty(json) && json.Trim() != "[]")
                {
                    var mmtMonitors = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                    if (mmtMonitors != null && mmtMonitors.Count > 0)
                    {
                        var result = new List<object>();
                        foreach (var mmt in mmtMonitors)
                        {
                            var res = mmt.GetValueOrDefault("resolution") ?? mmt.GetValueOrDefault("Resolution");
                            res.TryParseResolution(out int width, out int height);
                            var pos = mmt.GetValueOrDefault("left-top") ?? mmt.GetValueOrDefault("Left-Top");
                            pos.TryParsePosition(out int x, out int y);
                            result.Add(new
                            {
                                index = result.Count,
                                name = mmt.GetFriendlyMonitorName(),
                                deviceName = mmt.GetValueOrDefault("name") ?? "",
                                isPrimary = string.Equals(mmt.GetValueOrDefault("primary"), "Yes", StringComparison.OrdinalIgnoreCase) || string.Equals(mmt.GetValueOrDefault("Primary"), "Yes", StringComparison.OrdinalIgnoreCase),
                                bounds = new { x, y, width, height }
                            });
                        }
                        return result;
                    }
                }

                Console.WriteLine("[ScreenshotService] ListScreens: MultiMonitorToolService returned nothing. Falling back to local Win32.");
                var screens = new List<object>();
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.Rect lprcMonitor, IntPtr dwData) =>
                {
                    var mi = NativeMethods.MONITORINFOEX.Create();
                    if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                    {
                        screens.Add(new
                        {
                            index = screens.Count,
                            name = mi.szDevice,
                            deviceName = mi.szDevice,
                            isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                            bounds = new { x = mi.rcMonitor.Left, y = mi.rcMonitor.Top, width = mi.rcMonitor.Right - mi.rcMonitor.Left, height = mi.rcMonitor.Bottom - mi.rcMonitor.Top },
                            workArea = new { x = mi.rcWork.Left, y = mi.rcWork.Top, width = mi.rcWork.Right - mi.rcWork.Left, height = mi.rcWork.Bottom - mi.rcWork.Top }
                        });
                    }
                    return true;
                }, IntPtr.Zero);
                return screens;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenshotService] ListScreens Exception: {ex.Message}");
                return new List<object>();
            }
        }

        public List<string> ListDesktops()
        {
            List<string> desktops = new List<string>();
            try
            {
                IntPtr hWinSta = OpenWindowStation("WinSta0", false, MAXIMUM_ALLOWED);
                if (hWinSta != IntPtr.Zero)
                {
                    EnumDesktops(hWinSta, (name, param) =>
                    {
                        desktops.Add(name);
                        return true;
                    }, IntPtr.Zero);
                    CloseWindowStation(hWinSta);
                }
            }
            catch { }
            return desktops;
        }

        public async Task StartStreamingProcess(string display, int quality, int fps, Stream outputStream, System.Threading.CancellationToken ct)
        {
            string resolvedDisplay = await _monitorService.ResolveMonitorName(display);
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "WinAgent.exe";
            var token = Config.Get("token", "-token", "WINAGENT_TOKEN");

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            var args = $"--stream-helper --display \"{resolvedDisplay}\" --quality {quality} --fps {fps} --port {port} -token {token}";
            Console.WriteLine($"[ScreenshotService] Starting Stream Helper on port {port}: {args}");

            Process? process = null;
            try
            {
                if (Global.IsServiceMode)
                {
                    uint sessionId = _processService.GetActiveConsoleSessionId();
                    process = await _processService.StartProcessForStreaming(exePath, args, sessionId.ToString());
                }
                else
                {
                    process = await _processService.StartProcessForStreaming(exePath, args);
                }

                var acceptTask = listener.AcceptTcpClientAsync(ct);
                var completedTask = await Task.WhenAny(acceptTask.AsTask(), Task.Delay(5000, ct));

                if (completedTask != acceptTask.AsTask())
                {
                    throw new TimeoutException("Stream helper failed to connect back within 5s.");
                }

                using var client = await acceptTask;
                using var networkStream = client.GetStream();
                await networkStream.CopyToAsync(outputStream, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenshotService] Streaming error: {ex.Message}");
            }
            finally
            {
                listener.Stop();
                if (process != null && !process.HasExited) try { process.Kill(true); } catch { }
                process?.Dispose();
            }
        }
    }
}
