using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinAgent.Models;

namespace WinAgent;

public class TrayIpcClient
{
    private readonly string _token;
    private readonly Action<string> _logger;
    private NamedPipeClientStream? _client;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private bool _isAuthenticated;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage?>> _pendingRequests = new();
    private readonly object _sendLock = new();

    public bool IsConnected => _client?.IsConnected == true && _isAuthenticated;

    public TrayIpcClient(string token, Action<string> logger)
    {
        _token = token;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ConnectionLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        Disconnect();
    }

    private void Disconnect()
    {
        _isAuthenticated = false;
        try
        {
            _writer?.Dispose();
            _writer = null;
            _client?.Dispose();
            _client = null;
        }
        catch { }

        // Fail all pending requests
        foreach (var key in _pendingRequests.Keys)
        {
            if (_pendingRequests.TryRemove(key, out var tcs))
            {
                tcs.TrySetResult(null);
            }
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        _logger("Starting Tray IPC Client Connection Loop...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger("Connecting to Named Pipe: winagent...");
                var client = new NamedPipeClientStream(".", "winagent", PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(5000, cancellationToken);
                _client = client;

                _logger("IPC Client connected. Initiating authentication handshake...");

                var reader = new StreamReader(client, System.Text.Encoding.UTF8);
                var writer = new StreamWriter(client, System.Text.Encoding.UTF8) { AutoFlush = true };
                _writer = writer;

                // Send Auth message
                var authMsg = new IpcMessage
                {
                    Type = "Auth",
                    Token = _token
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(authMsg));

                // Read AuthResponse
                var authLine = await reader.ReadLineAsync(cancellationToken);
                if (authLine != null)
                {
                    var authResp = JsonSerializer.Deserialize<IpcMessage>(authLine);
                    if (authResp?.Type == "AuthResponse" && authResp.Success == true)
                    {
                        _isAuthenticated = true;
                        _logger("IPC Client authenticated successfully!");
                    }
                    else
                    {
                        _logger("IPC Client authentication failed. Server rejected token.");
                        Disconnect();
                        await Task.Delay(5000, cancellationToken);
                        continue;
                    }
                }
                else
                {
                    _logger("IPC handshake read failed. Connection closed by host.");
                    Disconnect();
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }

                // Main loop
                while (!cancellationToken.IsCancellationRequested && client.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) break;

                    try
                    {
                        var msg = JsonSerializer.Deserialize<IpcMessage>(line);
                        if (msg == null) continue;

                        if (msg.Type == "Response")
                        {
                            if (msg.Id != null && _pendingRequests.TryGetValue(msg.Id, out var tcs))
                            {
                                tcs.TrySetResult(msg);
                            }
                        }
                        else if (msg.Type == "Request")
                        {
                            _ = Task.Run(async () =>
                            {
                                var response = new IpcMessage { Id = msg.Id, Type = "Response" };
                                try
                                {
                                    if (msg.Path == "tray/notify")
                                    {
                                        var payload = JsonSerializer.Deserialize<ToastPayload>(msg.Payload ?? "{}");
                                        if (payload != null)
                                        {
                                            // ── Banner: route through the shared TrayBannerService ──────────
                                            if (payload.UseBanner)
                                            {
                                                _ = TrayBannerService.ShowAsync(
                                                    title: payload.Title ?? "WinAgent",
                                                    message: payload.Message ?? "",
                                                    position: payload.BannerPosition ?? "TopLeft",
                                                    imagePath: payload.Data?.Image,
                                                    durationSeconds: payload.Data?.Duration > 0
                                                        ? payload.Data.Duration
                                                        : (payload.Timeout > 0 ? payload.Timeout / 1000 : 3),
                                                    callback: payload.Callback ?? payload.ClickAction,
                                                    priority: payload.Priority,
                                                    ding: payload.Ding
                                                ).ContinueWith(t =>
                                                {
                                                    if (t.IsFaulted)
                                                        _logger($"Banner error: {t.Exception?.InnerException?.Message}");
                                                }, TaskScheduler.Default);
                                            }

                                            // ── All other notification types: per-request STA thread ────────
                                            bool hasNonBannerType = payload.UseMessageBox || payload.UseToast
                                                || payload.UseXSOverlay || payload.UseOVRToolkit;

                                            if (hasNonBannerType)
                                            {
                                                var thread = new Thread(() =>
                                                {
                                                    try
                                                    {
                                                        Modern_Windows_Message_Box_Generator.CLI.Program.ExecuteArgsAsync(new Modern_Windows_Message_Box_Generator.CLI.MessageBoxArgs
                                                        {
                                                            Title = payload.Title ?? "WinAgent",
                                                            Message = payload.Message ?? "",
                                                            UseDialog = payload.UseMessageBox,
                                                            UseBanner = false, // already handled above
                                                            UseXSOverlay = payload.UseXSOverlay,
                                                            UseOVRToolkit = payload.UseOVRToolkit,
                                                            UseToast = payload.UseToast,
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
                                                            ImagePath = payload.Data?.Image ?? "",
                                                            Duration = payload.Data?.Duration > 0 ? payload.Data.Duration : (payload.Timeout > 0 ? payload.Timeout / 1000 : 3)
                                                        }).Wait();
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        _logger($"Error running enhanced notification: {ex.Message}");
                                                    }
                                                });
                                                thread.SetApartmentState(ApartmentState.STA);
                                                thread.Start();
                                            }

                                            response.Success = true;
                                            response.Payload = "Notification triggered";
                                        }
                                        else
                                        {
                                            response.Success = false;
                                            response.Error = "Invalid toast payload";
                                        }
                                    }
                                    else if (msg.Path == "tray/screenshot")
                                    {
                                        var req = JsonSerializer.Deserialize<TrayScreenshotRequest>(msg.Payload ?? "{}");
                                        var display = req?.Display ?? "all";
                                        var quality = req?.Quality ?? 75;
                                        var format = req?.Format ?? "png";

                                        byte[]? bytes = null;
                                        var thread = new Thread(() =>
                                        {
                                            try
                                            {
                                                bytes = WinAgent.Utils.SessionHelper.CaptureScreenshotBytes(display, quality, format);
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger($"Error capturing screenshot in Tray: {ex.Message}");
                                            }
                                        });
                                        thread.SetApartmentState(ApartmentState.STA);
                                        thread.Start();
                                        thread.Join();

                                        if (bytes != null)
                                        {
                                            response.Success = true;
                                            response.Payload = Convert.ToBase64String(bytes);
                                        }
                                        else
                                        {
                                            response.Success = false;
                                            response.Error = "Failed to capture screenshot in Tray session context.";
                                        }
                                    }
                                    else
                                    {
                                        response.Success = false;
                                        response.Error = $"Unknown tray path: {msg.Path}";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    response.Success = false;
                                    response.Error = ex.Message;
                                }

                                try
                                {
                                    var respLine = JsonSerializer.Serialize(response);
                                    lock (_sendLock)
                                    {
                                        if (client.IsConnected && _writer != null)
                                        {
                                            _writer.WriteLine(respLine);
                                        }
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"Error parsing IPC line: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger($"IPC Client Error: {ex.Message}. Reconnecting in 3s...");
            }
            finally
            {
                Disconnect();
            }

            await Task.Delay(3000, cancellationToken);
        }
    }

    public async Task<IpcMessage?> SendRequestAsync(string path, string payload, int timeoutMs = 5000)
    {
        var client = _client;
        var writer = _writer;

        if (client == null || !client.IsConnected || !_isAuthenticated || writer == null)
        {
            return null;
        }

        var messageId = Guid.NewGuid().ToString();
        var message = new IpcMessage
        {
            Id = messageId,
            Type = "Request",
            Path = path,
            Payload = payload
        };

        var tcs = new TaskCompletionSource<IpcMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[messageId] = tcs;

        try
        {
            var line = JsonSerializer.Serialize(message);
            lock (_sendLock)
            {
                if (client.IsConnected)
                {
                    writer.WriteLine(line);
                }
            }

            using var delayCt = new CancellationTokenSource(timeoutMs);
            using (delayCt.Token.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task;
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(messageId, out _);
        }
    }
}

public class TrayScreenshotRequest
{
    public string Display { get; set; } = "all";
    public int Quality { get; set; } = 75;
    public string Format { get; set; } = "png";
}
