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
        // Unify type vs msgbox_type
        var isMessageBoxButtonType = request.Type.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                                     request.Type.Equals("okcancel", StringComparison.OrdinalIgnoreCase) ||
                                     request.Type.Equals("yesno", StringComparison.OrdinalIgnoreCase) ||
                                     request.Type.Equals("yesnocancel", StringComparison.OrdinalIgnoreCase) ||
                                     request.Type.Equals("retrycancel", StringComparison.OrdinalIgnoreCase) ||
                                     request.Type.Equals("abortretryignore", StringComparison.OrdinalIgnoreCase);
        if (isMessageBoxButtonType)
        {
            request.MessageBoxType = request.Type;
            request.Type = "messagebox";
        }

        // Handle multiple notification types based on flags or the 'Type' string
        bool useToast = request.UseToast ?? request.Type.Contains("toast", StringComparison.OrdinalIgnoreCase);
        bool useMessageBox = request.UseMessageBox ?? request.Type.Contains("messagebox", StringComparison.OrdinalIgnoreCase);
        bool useBanner = request.UseBanner ?? request.Type.Contains("banner", StringComparison.OrdinalIgnoreCase);
        bool useXSOverlay = request.UseXSOverlay ?? request.Type.Contains("xsoverlay", StringComparison.OrdinalIgnoreCase);
        bool useOVRToolkit = request.UseOVRToolkit ?? request.Type.Contains("ovrtoolkit", StringComparison.OrdinalIgnoreCase);

        // Default to Toast if nothing specified
        if (!useToast && !useMessageBox && !useBanner && !useXSOverlay && !useOVRToolkit) useToast = true;

        // Unify timeout and duration
        if (request.Duration == 0 && request.Timeout > 0)
        {
            request.Duration = request.Timeout / 1000;
        }
        else if (request.Timeout == 0 && request.Duration > 0)
        {
            request.Timeout = request.Duration * 1000;
        }

        // Unify callback & click action
        var clickUrl = request.ClickAction ?? request.Callback;
        if (!string.IsNullOrEmpty(clickUrl))
        {
            request.ClickAction = clickUrl;
            request.Callback = clickUrl;
        }

        // Unify icon & msgbox_icon
        var iconVal = request.Icon ?? request.MessageBoxIcon;
        if (!string.IsNullOrEmpty(iconVal))
        {
            request.MessageBoxIcon = iconVal;
        }

        var data = request.Data ?? new NotificationData();

        // Merge top-level convenience fields into Data sub-object
        if (string.IsNullOrEmpty(data.Image) && !string.IsNullOrEmpty(request.Image))
            data.Image = request.Image;
        if (string.IsNullOrEmpty(data.Tag) && !string.IsNullOrEmpty(request.Tag))
            data.Tag = request.Tag;
        if (string.IsNullOrEmpty(data.Group) && !string.IsNullOrEmpty(request.Group))
            data.Group = request.Group;
        if (data.ClickAction == NotificationData.NoAction && !string.IsNullOrEmpty(clickUrl))
            data.ClickAction = clickUrl;
        if (data.Duration == 0 && request.Duration > 0)
            data.Duration = request.Duration;
        if (request.Persistent && !data.Sticky)
            data.Sticky = true;

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
            MessageBoxIcon = iconVal,
            Timeout = request.Timeout,
            Classic = request.Classic,
            Callback = clickUrl,
            Flash = request.Flash,
            Ding = request.Ding,
            UseXSOverlay = useXSOverlay,
            UseOVRToolkit = useOVRToolkit,
            UseToast = useToast,
            Persistent = request.Persistent,
            Priority = request.Priority,
            Tag = request.Tag ?? data.Tag,
            Group = request.Group ?? data.Group,
            ClickAction = clickUrl ?? (data.ClickAction != NotificationData.NoAction ? data.ClickAction : null),
            Image = request.Image ?? data.Image
        };
    }

    public async Task ShowNotificationAsync(ToastPayload payload)
    {
        if (payload == null) return;

        try
        {
            if (payload.Data == null) payload.Data = new NotificationData();

            // Merge top-level properties from direct deserialization (e.g., from MQTT payloads)
            if (string.IsNullOrEmpty(payload.Data.Image) && !string.IsNullOrEmpty(payload.Image))
                payload.Data.Image = payload.Image;
            if (string.IsNullOrEmpty(payload.Data.Tag) && !string.IsNullOrEmpty(payload.Tag))
                payload.Data.Tag = payload.Tag;
            if (string.IsNullOrEmpty(payload.Data.Group) && !string.IsNullOrEmpty(payload.Group))
                payload.Data.Group = payload.Group;
            if (payload.Data.ClickAction == NotificationData.NoAction && !string.IsNullOrEmpty(payload.ClickAction))
                payload.Data.ClickAction = payload.ClickAction;
            if (payload.Data.Duration == 0 && payload.Timeout > 0)
                payload.Data.Duration = payload.Timeout / 1000;
            if (payload.Persistent && !payload.Data.Sticky)
                payload.Data.Sticky = true;

            // Unify timeout/duration back-propagation
            if (payload.Timeout == 0 && payload.Data.Duration > 0)
                payload.Timeout = payload.Data.Duration * 1000;

            // Unify callback/click_action back-propagation
            var unifiedClickUrl = payload.ClickAction ?? payload.Callback ?? (payload.Data.ClickAction != NotificationData.NoAction ? payload.Data.ClickAction : null);
            if (!string.IsNullOrEmpty(unifiedClickUrl))
            {
                payload.ClickAction = unifiedClickUrl;
                payload.Callback = unifiedClickUrl;
                payload.Data.ClickAction = unifiedClickUrl;
            }

            // 1. If Tray App is connected over authed Named Pipe IPC, delegate everything to it!
            if (IpcServerService.IsTrayConnected)
            {
                _logger.LogInformation("Tray is connected. Delegating notification via IPC...");
                try
                {
                    var ipcResponse = await IpcServerService.SendRequestToTrayAsync("tray/notify", JsonSerializer.Serialize(payload), 5000);
                    if (ipcResponse != null && ipcResponse.Success == true)
                    {
                        _logger.LogInformation("Successfully sent notification to Tray via IPC.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send notification via IPC, falling back to direct execution.");
                }
            }

            // 2. Direct Fallback: Direct native calls in the Service process (NO external processes are spawned!)
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

            if (payload.UseMessageBox || payload.UseBanner)
            {
                _logger.LogInformation("Processing direct message box fallback (WTSSendMessage).");
                uint sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId != 0xFFFFFFFF && sessionId != 0)
                {
                    ShowWtsMessageBox(
                        sessionId,
                        payload.Title ?? "Notification",
                        payload.Message ?? "",
                        payload.MessageBoxType ?? "ok",
                        payload.MessageBoxIcon ?? "info",
                        payload.Timeout
                    );
                }
                else
                {
                    _logger.LogWarning("No active console session found to display direct message box.");
                }
            }

            if (payload.UseXSOverlay)
            {
                _logger.LogInformation("Processing direct XSOverlay notification.");
                SendXSOverlay(payload.Title ?? "Notification", payload.Message ?? "", payload.Timeout);
            }

            if (payload.UseOVRToolkit)
            {
                _logger.LogInformation("Processing direct OVRToolkit notification.");
                await SendOvrToolkitAsync(payload.Title ?? "Notification", payload.Message ?? "");
            }

            if (payload.UseToast)
            {
                _logger.LogInformation("Processing direct Windows Toast notification.");
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

                if (payload.Persistent || payload.Data?.Sticky == true) builder.SetToastScenario(ToastScenario.Reminder);
                else if (payload.Data?.Importance == NotificationData.ImportanceHigh) builder.SetToastScenario(ToastScenario.Alarm);

                var toast = builder.GetToastContent();
                var notification = new ToastNotification(toast.GetXml());

                var tagVal = !string.IsNullOrWhiteSpace(payload.Tag) ? payload.Tag : payload.Data?.Tag;
                var groupVal = !string.IsNullOrWhiteSpace(payload.Group) ? payload.Group : payload.Data?.Group;
                if (!string.IsNullOrWhiteSpace(tagVal)) notification.Tag = tagVal;
                if (!string.IsNullOrWhiteSpace(groupVal)) notification.Group = groupVal;

                if (payload.Data?.Duration > 0)
                    notification.ExpirationTime = DateTimeOffset.Now.AddSeconds(payload.Data.Duration);

                ToastNotificationManagerCompat.CreateToastNotifier().Show(notification);
            }
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
        var payload = new ToastPayload
        {
            Title = title,
            Message = message,
            UseToast = toast,
            UseMessageBox = messagebox,
            UseOVRToolkit = ovrtoolkit,
            UseXSOverlay = xsoverlay,
            MessageBoxType = type,
            MessageBoxIcon = icon,
            Timeout = timeoutMs,
            Data = new NotificationData()
        };

        if (!toast && !messagebox && !ovrtoolkit && !xsoverlay)
        {
            payload.UseToast = true;
        }

        await ShowNotificationAsync(payload);
        return "Notification triggered successfully";
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
