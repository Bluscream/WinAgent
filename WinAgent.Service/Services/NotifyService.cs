using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using WinAgent.Models;
using NotificationData = WinAgent.Models.NotificationData;

namespace WinAgent.Services;

public class NotifyService
{
    private readonly ProcessService _processService;
    private readonly ILogger<NotifyService> _logger;
    private static readonly Uri OVRT_WEBSOCKET_URI = new("ws://127.0.0.1:11450/api");
    private static ClientWebSocket? _ovrtWebsocket;
    private static readonly SemaphoreSlim _ovrtLock = new(1, 1);
    private const int XSO_PORT = 42069;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    public NotifyService(ProcessService processService, ILogger<NotifyService> logger)
    {
        _processService = processService;
        _logger = logger;
    }

    public ToastPayload MapRequestToPayload(NotifyRequest request)
    {
        // Handle multiple notification types based on flags or the 'Type' string
        bool useToast = request.UseToast ?? request.Type.Contains("toast", StringComparison.OrdinalIgnoreCase);
        bool useMessageBox = request.UseMessageBox ?? request.Type.Contains("messagebox", StringComparison.OrdinalIgnoreCase);
        bool useBanner = request.UseBanner ?? request.Type.Contains("banner", StringComparison.OrdinalIgnoreCase);
        bool useXSOverlay = request.UseXSOverlay ?? request.Type.Contains("xsoverlay", StringComparison.OrdinalIgnoreCase);
        bool useOVRToolkit = request.UseOVRToolkit ?? request.Type.Contains("ovrtoolkit", StringComparison.OrdinalIgnoreCase);

        // Default to Toast if nothing specified
        if (!useToast && !useMessageBox && !useBanner && !useXSOverlay && !useOVRToolkit) useToast = true;

        var data = request.Data ?? new NotificationData();
        if (string.IsNullOrEmpty(data.Image) && !string.IsNullOrEmpty(request.Image))
        {
            data.Image = request.Image;
        }

        return new ToastPayload
        {
            Title = request.Title,
            Message = request.Message,
            Data = data,
            UseMessageBox = useMessageBox,
            UseBanner = useBanner,
            BannerPosition = request.BannerPosition,
            Heading = request.Heading,
            Footer = request.Footer,
            Details = request.Details,
            Checkbox = request.Checkbox,
            MessageBoxType = request.MessageBoxType,
            MessageBoxIcon = request.MessageBoxIcon,
            Timeout = request.Timeout,
            Classic = request.Classic,
            Callback = request.Callback,
            Flash = request.Flash,
            Ding = request.Ding,
            UseXSOverlay = useXSOverlay,
            UseOVRToolkit = useOVRToolkit
        };
    }

    public async Task ShowNotificationAsync(ToastPayload payload)
    {
        if (payload == null) return;

        try
        {
            if (payload.Data == null) payload.Data = new NotificationData();

            if (payload.Message == "clear_notification")
            {
                if (!string.IsNullOrWhiteSpace(payload.Data.Tag) && !string.IsNullOrWhiteSpace(payload.Data.Group))
                    ToastNotificationManagerCompat.History.Remove(payload.Data.Tag, payload.Data.Group);
                else if (!string.IsNullOrWhiteSpace(payload.Data.Tag))
                    ToastNotificationManagerCompat.History.Remove(payload.Data.Tag);
                else
                    ToastNotificationManagerCompat.History.Clear();
                
                return;
            }

            if (payload.UseMessageBox || payload.UseBanner || payload.UseXSOverlay || payload.UseOVRToolkit)
            {
                _logger.LogInformation("Processing enhanced notification (MB: {MB}, Banner: {Banner}, XS: {XS}, OVR: {OVR})", 
                    payload.UseMessageBox, payload.UseBanner, payload.UseXSOverlay, payload.UseOVRToolkit);
                
                try
                {
                    var ipcResponse = await IpcServerService.SendRequestToTrayAsync("tray/notify", JsonSerializer.Serialize(payload), 5000);
                    if (ipcResponse != null && ipcResponse.Success == true)
                    {
                        _logger.LogInformation("Successfully sent enhanced notification to Tray via IPC.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send enhanced notification via IPC, falling back to process execution.");
                }
                var msgArgs = new Modern_Windows_Message_Box_Generator.CLI.MessageBoxArgs
                {
                    Title = payload.Title ?? "Notification",
                    Message = payload.Message ?? "",
                    UseDialog = payload.UseMessageBox,
                    UseBanner = payload.UseBanner,
                    UseXSOverlay = payload.UseXSOverlay,
                    UseOVRToolkit = payload.UseOVRToolkit,
                    Heading = payload.Heading ?? "",
                    Footer = payload.Footer ?? "",
                    Details = payload.Details ?? "",
                    Checkbox = payload.Checkbox ?? "",
                    Type = payload.MessageBoxType ?? "ok",
                    Icon = payload.MessageBoxIcon ?? "info",
                    Timeout = payload.Timeout,
                    Classic = payload.Classic,
                    CallbackUrl = payload.Callback ?? "",
                    Flash = payload.Flash,
                    Ding = payload.Ding,
                    BannerPos = payload.BannerPosition ?? "TopLeft",
                    ImagePath = payload.Data?.Image ?? "",
                    Duration = payload.Data?.Duration > 0 ? payload.Data.Duration : (payload.Timeout > 0 ? payload.Timeout / 1000 : 3)
                };

                uint sessionId = _processService.GetActiveConsoleSessionId();
                if (sessionId != 0xFFFFFFFF && sessionId != 0)
                {
                    _logger.LogInformation("Active console session found (Session {SessionId}). Spawning session helper.", sessionId);
                    
                    var helperExe = "Modern-Windows-Message-Box-Generator.CLI.exe";
                    if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, helperExe)))
                    {
                        helperExe = "msgbox.exe";
                    }
                    if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, helperExe)))
                    {
                        helperExe = "winagent.exe";
                    }

                    var jsonStr = JsonSerializer.Serialize(msgArgs);
                    var cmdArgs = $"--json \"{jsonStr.Replace("\"", "\\\"")}\"";

                    _ = Task.Run(async () => {
                        try {
                            await _processService.StartProcess(helperExe, cmdArgs, asUser: sessionId.ToString(), windowStyle: "hidden");
                        } catch (Exception ex) {
                            _logger.LogError(ex, "Error executing session-escaped enhanced notification");
                        }
                    });
                }
                else
                {
                    _logger.LogInformation("No active console session found or currently in user session. Executing directly.");
                    
                    _ = Task.Run(async () => {
                        try {
                            await Modern_Windows_Message_Box_Generator.CLI.Program.ExecuteArgsAsync(msgArgs);
                        } catch (Exception ex) {
                            _logger.LogError(ex, "Error executing direct enhanced notification");
                        }
                    });
                }
            }

            var builder = new ToastContentBuilder()
                .AddText(payload.Title ?? "Home Assistant")
                .AddText(payload.Message ?? "");

            if (payload.Data?.ClickAction != NotificationData.NoAction && !string.IsNullOrWhiteSpace(payload.Data?.ClickAction))
                builder.AddArgument("action", payload.Data.ClickAction);

            if (!string.IsNullOrWhiteSpace(payload.Data?.Image) && Uri.TryCreate(payload.Data.Image, UriKind.Absolute, out Uri? imageUrl))
                builder.AddHeroImage(imageUrl);

            if (!string.IsNullOrWhiteSpace(payload.Data?.IconUrl) && Uri.TryCreate(payload.Data.IconUrl, UriKind.Absolute, out Uri? iconUrl))
                builder.AddAppLogoOverride(iconUrl, ToastGenericAppLogoCrop.Default);

            if (payload.Data?.Actions != null && payload.Data.Actions.Count > 0)
            {
                foreach (var action in payload.Data.Actions)
                {
                    if (string.IsNullOrEmpty(action.Action)) continue;
                    
                    var button = new ToastButton().SetContent(action.Title).AddArgument("action", action.Action);
                    if (!string.IsNullOrWhiteSpace(action.Uri)) button.AddArgument("uri", action.Uri);
                    builder.AddButton(button);
                }
            }

            if (payload.Data?.Inputs != null && payload.Data.Inputs.Count > 0)
            {
                foreach (var input in payload.Data.Inputs)
                {
                    if (string.IsNullOrEmpty(input.Id)) continue;
                    builder.AddInputTextBox(input.Id, input.Text, input.Title);
                }
            }

            if (payload.Data?.Sticky == true) builder.SetToastScenario(ToastScenario.Reminder);
            else if (payload.Data?.Importance == NotificationData.ImportanceHigh) builder.SetToastScenario(ToastScenario.Alarm);

            var toast = builder.GetToastContent();
            var notification = new ToastNotification(toast.GetXml());

            if (!string.IsNullOrWhiteSpace(payload.Data?.Tag)) notification.Tag = payload.Data.Tag;
            if (!string.IsNullOrWhiteSpace(payload.Data?.Group)) notification.Group = payload.Data.Group;

            if (payload.Data?.Duration > 0)
                notification.ExpirationTime = DateTimeOffset.Now.AddSeconds(payload.Data.Duration);

            ToastNotificationManagerCompat.CreateToastNotifier().Show(notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling unified notification");
        }
    }

    public async Task<string> NotifyAsync(
        string title,
        string message,
        bool toast = false,
        bool messagebox = false,
        bool ovrtoolkit = false,
        bool xsoverlay = false,
        string type = "MB_OK",
        string icon = "MB_ICONINFORMATION",
        int timeoutMs = 5000)
    {
        var results = new List<string>();

        if (toast)
        {
            try { await SendToastAsync(title, message); results.Add("Toast sent"); }
            catch (Exception ex) { results.Add($"Toast failed: {ex.Message}"); }
        }

        if (messagebox)
        {
            try { await ShowMessageBoxAsync(title, message, type, icon, timeoutMs); results.Add("MessageBox shown"); }
            catch (Exception ex) { results.Add($"MessageBox failed: {ex.Message}"); }
        }

        if (ovrtoolkit)
        {
            try { if (await SendOvrToolkitAsync(title, message)) results.Add("OVRToolkit sent"); else results.Add("OVRToolkit failed (connect)"); }
            catch (Exception ex) { results.Add($"OVRToolkit failed: {ex.Message}"); }
        }

        if (xsoverlay)
        {
            try { if (SendXSOverlay(title, message, timeoutMs)) results.Add("XSOverlay sent"); else results.Add("XSOverlay failed"); }
            catch (Exception ex) { results.Add($"XSOverlay failed: {ex.Message}"); }
        }

        return string.Join(", ", results);
    }

    private async Task SendToastAsync(string title, string message)
    {
        var escapedTitle = title.Replace("\"", "`\"").Replace("'", "''");
        var escapedMessage = message.Replace("\"", "`\"").Replace("'", "''");

        // PowerShell script to send toast notification
        var script = $@"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$APP_ID = 'WinAgent Notifications'
$template = @'
<toast>
    <visual>
        <binding template=''ToastGeneric''>
            <text>{escapedTitle}</text>
            <text>{escapedMessage}</text>
        </binding>
    </visual>
</toast>
'@

$xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
$xml.LoadXml($template)
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($APP_ID).Show($toast)
";

        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new Exception("No active console session found");

        await _processService.StartProcess("powershell.exe", $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"", asUser: sessionId.ToString());
    }

    private async Task ShowMessageBoxAsync(string title, string message, string type, string icon, int timeoutMs)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new Exception("No active console session found");

        var msgArgs = new Modern_Windows_Message_Box_Generator.CLI.MessageBoxArgs
        {
            Title = title,
            Message = message,
            UseDialog = true,
            Type = type,
            Icon = icon,
            Timeout = timeoutMs
        };

        var jsonStr = JsonSerializer.Serialize(msgArgs);
        var cmdArgs = $"--json \"{jsonStr.Replace("\"", "\\\"")}\"";
        
        try
        {
            var helperExe = "Modern-Windows-Message-Box-Generator.CLI.exe";
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, helperExe)))
            {
                helperExe = "msgbox.exe";
            }
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, helperExe)))
            {
                helperExe = "winagent.exe";
            }
            await _processService.StartProcess(helperExe, cmdArgs, asUser: sessionId.ToString(), windowStyle: "hidden");
        }
        catch (Exception ex)
        {
            // Final fallback: Use WTSSendMessage directly
            ShowWtsMessageBox(sessionId, title, message, type, icon, timeoutMs);
            throw new Exception($"Helper {ex.Message}. Fallback to WTSSendMessage used.");
        }
    }

    private void ShowWtsMessageBox(uint sessionId, string title, string message, string type, string icon, int timeoutMs)
    {
        uint style = 0; // MB_OK
        if (type.Contains("OKCANCEL", StringComparison.OrdinalIgnoreCase)) style = 1;
        else if (type.Contains("ABORTRETRYIGNORE", StringComparison.OrdinalIgnoreCase)) style = 2;
        else if (type.Contains("YESNOCANCEL", StringComparison.OrdinalIgnoreCase)) style = 3;
        else if (type.Contains("YESNO", StringComparison.OrdinalIgnoreCase)) style = 4;
        else if (type.Contains("RETRYCANCEL", StringComparison.OrdinalIgnoreCase)) style = 5;

        if (icon.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || icon.Contains("HAND", StringComparison.OrdinalIgnoreCase) || icon.Contains("STOP", StringComparison.OrdinalIgnoreCase)) style |= 0x10;
        else if (icon.Contains("QUESTION", StringComparison.OrdinalIgnoreCase)) style |= 0x20;
        else if (icon.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || icon.Contains("EXCLAMATION", StringComparison.OrdinalIgnoreCase)) style |= 0x30;
        else if (icon.Contains("INFORMATION", StringComparison.OrdinalIgnoreCase) || icon.Contains("ASTERISK", StringComparison.OrdinalIgnoreCase)) style |= 0x40;

        // WTSSendMessageW expects length in bytes, including the null terminator for some versions/implementations
        uint titleLen = (uint)(title.Length + 1) * 2;
        uint msgLen = (uint)(message.Length + 1) * 2;
        WTSSendMessage(IntPtr.Zero, sessionId, title, titleLen, message, msgLen, style, (uint)timeoutMs / 1000, out _, false);
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessage(
        IntPtr hServer,
        uint SessionId,
        string pTitle,
        uint TitleLength,
        string pMessage,
        uint MessageLength,
        uint Style,
        uint Timeout,
        out uint pResponse,
        bool bWait);

    private async Task<bool> SendOvrToolkitAsync(string title, string message)
    {
        await _ovrtLock.WaitAsync();
        try
        {
            if (_ovrtWebsocket?.State != WebSocketState.Open)
            {
                _ovrtWebsocket?.Dispose();
                _ovrtWebsocket = new ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ovrtWebsocket.ConnectAsync(OVRT_WEBSOCKET_URI, cts.Token);
            }

            var hudMsg = new
            {
                messageType = "SendNotification",
                json = JsonSerializer.Serialize(new
                {
                    title = title,
                    body = message,
                    icon = "" // Placeholder for now
                })
            };

            var json = JsonSerializer.Serialize(hudMsg);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ovrtWebsocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _ovrtLock.Release();
        }
    }

    private bool SendXSOverlay(string title, string message, int timeoutMs)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var endPoint = new IPEndPoint(IPAddress.Loopback, XSO_PORT);

            var msg = new
            {
                messageType = 1,
                title = title,
                content = message,
                height = message.Length > 200 ? 200f : 150f,
                sourceApp = "WinAgent",
                timeout = timeoutMs / 1000f,
                audioPath = "",
                useBase64Icon = false,
                icon = "",
                opacity = 1.0f
            };

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(msg);
            socket.SendTo(jsonBytes, endPoint);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
