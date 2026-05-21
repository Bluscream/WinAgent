using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinAgent.Models;
using WinAgent.Utils;

namespace WinAgent.Services;

public class IpcServerService : BackgroundService
{
    private readonly TokenService _tokenService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IpcServerService> _logger;
    private static NamedPipeServerStream? _activeServer;
    private static readonly object _sendLock = new();

    public IpcServerService(TokenService tokenService, IServiceProvider serviceProvider, ILogger<IpcServerService> logger)
    {
        _tokenService = tokenService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<IpcMessage?>> PendingRequests = new();

    public static async Task<IpcMessage?> SendRequestToTrayAsync(string path, string payload, int timeoutMs = 5000)
    {
        var server = _activeServer;
        if (server == null || !server.IsConnected)
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
        PendingRequests[messageId] = tcs;

        try
        {
            var line = JsonSerializer.Serialize(message) + "\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            
            lock (_sendLock)
            {
                if (server.IsConnected)
                {
                    server.Write(bytes, 0, bytes.Length);
                    server.Flush();
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
            PendingRequests.TryRemove(messageId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting IPC Named Pipe Server...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    "winagent", 
                    PipeDirection.InOut, 
                    1, 
                    PipeTransmissionMode.Byte, 
                    PipeOptions.Asynchronous);

                _logger.LogDebug("IPC Named Pipe Server waiting for connection...");
                await server.WaitForConnectionAsync(stoppingToken);
                _logger.LogInformation("IPC Named Pipe Client connected!");

                _activeServer = server;

                using var reader = new StreamReader(server, System.Text.Encoding.UTF8);
                using var writer = new StreamWriter(server, System.Text.Encoding.UTF8) { AutoFlush = true };

                bool isAuthenticated = false;

                while (!stoppingToken.IsCancellationRequested && server.IsConnected)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line == null) break;

                    try
                    {
                        var msg = JsonSerializer.Deserialize<IpcMessage>(line);
                        if (msg == null) continue;

                        if (!isAuthenticated)
                        {
                            if (msg.Type == "Auth" && msg.Token == _tokenService.Token)
                            {
                                isAuthenticated = true;
                                _logger.LogInformation("IPC Client successfully authenticated.");
                                var authResponse = new IpcMessage { Type = "AuthResponse", Success = true };
                                await writer.WriteLineAsync(JsonSerializer.Serialize(authResponse));
                            }
                            else
                            {
                                _logger.LogWarning("IPC Client failed authentication. Closing connection.");
                                var authResponse = new IpcMessage { Type = "AuthResponse", Success = false, Error = "Invalid token" };
                                await writer.WriteLineAsync(JsonSerializer.Serialize(authResponse));
                                break;
                            }
                            continue;
                        }

                        if (msg.Type == "Response")
                        {
                            if (msg.Id != null && PendingRequests.TryGetValue(msg.Id, out var tcs))
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
                                    if (string.IsNullOrEmpty(msg.Path))
                                    {
                                        response.Success = false;
                                        response.Error = "Path is required";
                                    }
                                    else
                                    {
                                        var result = await FeatureEndpoints_Generated.ExecuteFeatureAsync(
                                            msg.Path, 
                                            msg.Payload ?? "", 
                                            _serviceProvider, 
                                            WinAgent.Common.Features.ExecutionSource.Ipc);
                                        
                                        response.Success = true;
                                        response.Payload = JsonSerializer.Serialize(result);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    response.Success = false;
                                    response.Error = ex.Message;
                                }

                                try
                                {
                                    var respLine = JsonSerializer.Serialize(response) + "\n";
                                    var respBytes = System.Text.Encoding.UTF8.GetBytes(respLine);
                                    lock (_sendLock)
                                    {
                                        if (server.IsConnected)
                                        {
                                            server.Write(respBytes, 0, respBytes.Length);
                                            server.Flush();
                                        }
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing IPC message line");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC Named Pipe Server encountered an error. Restarting listener...");
                await Task.Delay(2000, stoppingToken);
            }
            finally
            {
                _activeServer = null;
            }
        }
    }
}
