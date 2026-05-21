using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinAgent.Services;
using WinAgent.Utils;
using Serilog;
using Microsoft.OpenApi.Models;
using ModelContextProtocol.Server;
using ModelContextProtocol.AspNetCore;
using System.Text.Json;

namespace WinAgent;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static async Task Main(string[] args)
    {
        if (Global.IsAnyHelper)
        {
            SessionHelper.Run(args);
            return;
        }

        AttachConsole(ATTACH_PARENT_PROCESS);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService();
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddJsonFile("WinAgent.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        Config.Initialize(builder.Configuration);

        if (Global.IsInstall)
        {
            EnsureInstallationEnvironmentVariables();
        }

        // Configure Logging
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(baseDir, "logs", "winagent.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();

        var token = Config.Get("token", "-token", "WINAGENT_TOKEN");
        
        if (string.IsNullOrEmpty(token))
        {
            Log.Fatal("CRITICAL ERROR: No WINAGENT_TOKEN provided. The application cannot start without an authentication token for security reasons. Please set the WINAGENT_TOKEN environment variable or provide it via appsettings.json.");
            throw new InvalidOperationException("WINAGENT_TOKEN is required.");
        }

        // Configure Kestrel
        var portStr = Config.Get("port", "WINAGENT_PORT");
        if (string.IsNullOrEmpty(portStr)) portStr = "23482";
        var port = int.Parse(portStr);
        
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port);
        });

        builder.Services.Configure<WinAgent.Models.MqttOptions>(options =>
        {
            options.Ip = Config.Get("Mqtt:Ip", "MQTT_IP");
            if (string.IsNullOrEmpty(options.Ip)) options.Ip = "127.0.0.1";

            var portVal = Config.Get("Mqtt:Port", "MQTT_PORT");
            options.Port = int.TryParse(portVal, out int p) ? p : 1883;

            options.User = Config.Get("Mqtt:User", "MQTT_USER");
            options.Password = Config.Get("Mqtt:Password", "MQTT_PW");
            options.EntityId = Config.Get("Mqtt:EntityId", "MQTT_ENTITY_ID");
            if (string.IsNullOrEmpty(options.EntityId)) options.EntityId = Global.SafeMachineName;
        });

        builder.Services.AddSingleton(new TokenService(token));
        
        builder.Services.AddAuthentication("Token")
            .AddScheme<TokenAuthenticationSchemeOptions, TokenAuthenticationHandler>("Token", options => { });
        builder.Services.AddAuthorization();
        
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "WinAgent API", Version = "v1" });
            c.AddSecurityDefinition("Token", new OpenApiSecurityScheme
            {
                Description = "Token Authentication using 'Authorization: Bearer <token>' header or '?token=<token>' query parameter",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer"
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Token" }
                    },
                    Array.Empty<string>()
                }
            });
        });

        // Add IpcMcp Services
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ProcessService>();
        builder.Services.AddSingleton<RegistryService>();
        builder.Services.AddSingleton<WindowsService>();
        builder.Services.AddSingleton<LogonRegistryService>();
        builder.Services.AddSingleton<DeviceService>();
        builder.Services.AddSingleton<ScreenshotService>();
        builder.Services.AddSingleton<MultiMonitorToolService>();
        builder.Services.AddSingleton<NamedPipeService>();
        builder.Services.AddSingleton<MemoryMappedFileService>();
        builder.Services.AddSingleton<ComService>();
        builder.Services.AddSingleton<AudioService>();
        builder.Services.AddSingleton<ServiceService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<NotifyService>();
        builder.Services.AddSingleton<PInvokeService>();
        builder.Services.AddSingleton<HardwareMonitorService>();
        builder.Services.AddSingleton<McpService>();
        builder.Services.AddSingleton<IpcServerService>();

        // Register Features
        builder.Services.AddSingleton<WinAgent.Features.ScreenshotFeature>();
        builder.Services.AddSingleton<WinAgent.Features.ShutdownFeature>();
        builder.Services.AddSingleton<WinAgent.Features.RebootFeature>();
        builder.Services.AddSingleton<WinAgent.Features.LockFeature>();
        builder.Services.AddSingleton<WinAgent.Features.LogoutFeature>();
        builder.Services.AddSingleton<WinAgent.Features.LoginFeature>();
        builder.Services.AddSingleton<WinAgent.Features.TypeLogonFeature>();
        builder.Services.AddSingleton<WinAgent.Features.ClearCredentialsFeature>();

        // IPC Features
        builder.Services.AddSingleton<WinAgent.Features.NamedPipeFeature>();
        builder.Services.AddSingleton<WinAgent.Features.MappedFileFeature>();
        builder.Services.AddSingleton<WinAgent.Features.RegistryFeature>();
        builder.Services.AddSingleton<WinAgent.Features.ComFeature>();
        builder.Services.AddSingleton<WinAgent.Features.SearchRegistryFeature>();

        // Process / System List Features
        builder.Services.AddSingleton<WinAgent.Features.StartProcessFeature>();
        builder.Services.AddSingleton<WinAgent.Features.ListFeature>();

        // Mcp Features
        builder.Services.AddSingleton<WinAgent.Features.StopMcpFeature>();
        builder.Services.AddSingleton<WinAgent.Features.RestartMcpFeature>();

        // System Update Feature
        builder.Services.AddSingleton<WinAgent.Features.UpdateFeature>();

        // Audio Feature
        builder.Services.AddSingleton<WinAgent.Features.AudioFeature>();

        // Displays Feature
        builder.Services.AddSingleton<WinAgent.Features.DisplaysFeature>();

        // Devices Feature
        builder.Services.AddSingleton<WinAgent.Features.DevicesFeature>();

        // Notify Feature
        builder.Services.AddSingleton<WinAgent.Features.NotifyFeature>();

        // Capture/Screenshot Features
        builder.Services.AddSingleton<WinAgent.Features.CaptureStreamFeature>();
        builder.Services.AddSingleton<WinAgent.Features.ListScreensFeature>();

        // Hardware Features
        builder.Services.AddSingleton<WinAgent.Features.ListHardwareFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetSensorsFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetSensorValueFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetHardwareDetailFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetSystemSummaryFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetCpuInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetGpuInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetMemoryInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetStorageInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetNetworkInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetBatteryInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetFanInfoFeature>();
        builder.Services.AddSingleton<WinAgent.Features.GetPowerInfoFeature>();

        // Configure CORS
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .WithExposedHeaders("Content-Type", "Cache-Control", "Last-Event-ID");
            });
        });

        // Register MCP Server
        builder.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithToolsFromAssembly();

        // Add Core Services
        builder.Services.AddSingleton<IDiscoveryService, DiscoveryService>();
        builder.Services.AddSingleton<IMqttManager, MqttManager>();
        builder.Services.AddSingleton<IPersistenceService, PersistenceService>();
        
        // Background tasks
        builder.Services.AddSingleton<SystemMonitorService>();
        builder.Services.AddSingleton<ShutdownBlockerService>();
        builder.Services.AddSingleton<ForceActionService>();
        builder.Services.AddSingleton<NotificationReceiverService>();
        builder.Services.AddSingleton<ActionExecutorService>();
        builder.Services.AddSingleton<ActionCenterPollerService>();
        builder.Services.AddSingleton<TrayStarterService>();

        builder.Host.UseWindowsService();

        // Use Global setup flags
        bool install = Global.IsInstall;
        bool uninstall = Global.IsUninstall;
        bool isAdmin = Global.IsAdmin;

        if ((install || uninstall || Global.IsStart || Global.IsStop) && isAdmin)
        {
            // Create a temporary logger for setup tasks
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
            var setupLogger = loggerFactory.CreateLogger<PersistenceService>();
            var persistence = new PersistenceService(setupLogger);
            string serviceName = "WinAgent";
            
            if (uninstall || Global.IsStop || (install && Global.IsStop))
            {
                if (ServiceHelper.IsServiceInstalled(serviceName))
                {
                    Log.Information("Stopping {Service} service...", serviceName);
                    try {
                        ServiceHelper.StopService(serviceName);
                        // Wait for service to stop
                        int retries = 20;
                        while (ServiceHelper.IsServiceRunning(serviceName) && retries-- > 0) Thread.Sleep(500);
                    } catch (Exception ex) { Log.Warning("Failed to stop service: {Message}", ex.Message); }
                }
                
                if (uninstall)
                {
                    persistence.Uninstall();
                    if (!install) return; // Exit if only uninstalling
                }
            }
            
            if (install)
            {
                persistence.EnsureServiceSafeBoot();
                persistence.EnsureFirewallRule(port);
                persistence.EnsureMoreStatesTriggers();
            }

            if (Global.IsStart || (install && !Global.IsStop))
            {
                if (ServiceHelper.IsServiceInstalled(serviceName))
                {
                    Log.Information("Starting {Service} service...", serviceName);
                    try { ServiceHelper.StartService(serviceName); }
                    catch (Exception ex) { Log.Warning("Failed to start service: {Message}", ex.Message); }
                }
            }
            
            // If we are JUST doing setup/service control without starting, exit
            if (!args.Contains("--run") && !args.Contains("--entity-state") && !args.Contains("--event"))
            {
                Log.Information("Task complete. Exiting.");
                return;
            }
        }

        // Handle one-off event firing
        var eventJson = args.SkipWhile(a => a != "--event").Skip(1).FirstOrDefault();
        if (eventJson != null && eventJson.StartsWith("-")) eventJson = null;

        if (!string.IsNullOrEmpty(eventJson))
        {
            try 
            {
                var baseUrl = $"http://127.0.0.1:{port}";
                var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                
                string finalJson = eventJson;
                try { JsonDocument.Parse(eventJson); }
                catch { finalJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["event"] = eventJson }); }

                var content = new StringContent(finalJson, System.Text.Encoding.UTF8, "application/json");
                var resp = client.PostAsync($"{baseUrl}/api/event", content).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                    Log.Information("Fired event successfully via service API. Exiting.");
                else
                    Log.Warning("Failed to fire event via API: {StatusCode}. Is the service running?", resp.StatusCode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to connect to local service API to fire event.");
            }

            return;
        }

        // Handle one-off state reporting
        var entityState = args.SkipWhile(a => a != Global.Args.EntityState).Skip(1).FirstOrDefault();
        if (entityState != null && entityState.StartsWith("-")) entityState = null;

        if (!string.IsNullOrEmpty(entityState))
        {
            var attributes = args.SkipWhile(a => a != Global.Args.EntityAttributes).Skip(1).FirstOrDefault();
            
            try 
            {
                var baseUrl = $"http://127.0.0.1:{port}";
                var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var url = $"{baseUrl}/api/state?state={Uri.EscapeDataString(entityState)}";
                if (!string.IsNullOrEmpty(attributes)) url += $"&attributes={Uri.EscapeDataString(attributes)}";
                
                var resp = client.PostAsync(url, null).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                    Log.Information("Reported state '{State}' to service API. Exiting.", entityState);
                else
                    Log.Warning("Failed to report state via API: {StatusCode}. Is the service running?", resp.StatusCode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to connect to local service API to report state.");
            }

            return;
        }
        
        builder.Services.AddHostedService(p => p.GetRequiredService<SystemMonitorService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<NotificationReceiverService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<ActionExecutorService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<ForceActionService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<ActionCenterPollerService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<TrayStarterService>());
        builder.Services.AddHostedService(p => (MqttManager)p.GetRequiredService<IMqttManager>());
        builder.Services.AddHostedService(p => p.GetRequiredService<ShutdownBlockerService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<IpcServerService>());
        builder.Services.AddHostedService<MqttFeatureBindingService>();

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        var app = builder.Build();

        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "WinAgent API v1");
            c.RoutePrefix = "docs";
        });

        app.MapControllers();
        app.MapMcp("/mcp").RequireAuthorization();
        app.MapFeatures();
        
        app.MapGet("/api/docs", (string format = "json") => 
        {
            var features = WinAgent.Common.Features.FeatureDocsRegistry.Features;
            if (format?.ToLower() == "md")
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# WinAgent API Documentation");
                foreach (var f in features)
                {
                    sb.AppendLine($"## {f["Path"]}");
                    sb.AppendLine($"**Description**: {f["Description"]}");
                    sb.AppendLine($"**Request Object**: `{f["RequestType"]}`");
                    sb.AppendLine("### Arguments");
                    var args = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)f["Arguments"];
                    foreach (var arg in args)
                    {
                        sb.AppendLine($"- `{arg["Name"]}` ({arg["Type"]})");
                    }
                    sb.AppendLine();
                }
                return Results.Text(sb.ToString(), "text/markdown");
            }
            return Results.Json(features);
        });

        app.MapGet("/", () => Results.Redirect("/docs"));

        Log.Information("Starting Web Host...");
        app.Run();
    }

    private static void EnsureInstallationEnvironmentVariables()
    {
        try
        {
            var existingToken = Environment.GetEnvironmentVariable("WINAGENT_TOKEN", EnvironmentVariableTarget.Machine);
            if (string.IsNullOrEmpty(existingToken))
            {
                existingToken = Environment.GetEnvironmentVariable("WINAGENT_TOKEN", EnvironmentVariableTarget.User);
            }

            if (string.IsNullOrEmpty(existingToken))
            {
                var tokenBytes = new byte[16];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(tokenBytes);
                }
                existingToken = Convert.ToHexString(tokenBytes).ToLowerInvariant();
                Environment.SetEnvironmentVariable("WINAGENT_TOKEN", existingToken, EnvironmentVariableTarget.Machine);
            }

            var existingPort = Environment.GetEnvironmentVariable("WINAGENT_PORT", EnvironmentVariableTarget.Machine);
            if (string.IsNullOrEmpty(existingPort))
            {
                existingPort = Environment.GetEnvironmentVariable("WINAGENT_PORT", EnvironmentVariableTarget.User);
            }

            if (string.IsNullOrEmpty(existingPort))
            {
                existingPort = "23482";
                Environment.SetEnvironmentVariable("WINAGENT_PORT", existingPort, EnvironmentVariableTarget.Machine);
            }

            // Set in current process so immediate execution has access
            Environment.SetEnvironmentVariable("WINAGENT_TOKEN", existingToken, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("WINAGENT_PORT", existingPort, EnvironmentVariableTarget.Process);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Setup] Failed to ensure system-wide environment variables during install: {ex.Message}");
        }
    }
}
