using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinAgent.Common.Features;
using WinAgent.Services;
using WinAgent.Utils;

namespace WinAgent.Services
{
    public class MqttFeatureBindingService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IMqttManager _mqttManager;
        private readonly ILogger<MqttFeatureBindingService> _logger;

        public MqttFeatureBindingService(IServiceProvider serviceProvider, IMqttManager mqttManager, ILogger<MqttFeatureBindingService> logger)
        {
            _serviceProvider = serviceProvider;
            _mqttManager = mqttManager;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait for MQTT to connect before subscribing
            while (!_mqttManager.IsConnected && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(2000, stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) return;

            var uniqueId = Global.UniqueId;
            var deviceInfo = new
            {
                identifiers = new[] { uniqueId },
                name = Global.MachineName,
                manufacturer = "Bluscream",
                model = "WinAgent",
                sw_version = "1.0.0"
            };

            // Discover all types implementing IFeatureDefinition in the entry assembly (or the assembly of LogonFeatures / ScreenshotFeature)
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly == null) return;

            var featureTypes = entryAssembly.GetTypes()
                .Where(t => typeof(IFeatureDefinition).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .ToList();

            foreach (var type in featureTypes)
            {
                // Process MqttButtonAttributes
                var buttonAttrs = type.GetCustomAttributes<MqttButtonAttribute>();
                foreach (var attr in buttonAttrs)
                {
                    await RegisterButtonAsync(type, attr.Id, uniqueId, deviceInfo);
                }

                // Process MqttSwitchAttributes
                var switchAttrs = type.GetCustomAttributes<MqttSwitchAttribute>();
                foreach (var attr in switchAttrs)
                {
                    await RegisterSwitchAsync(type, attr.Id, uniqueId, deviceInfo);
                }

                // Process MqttSelectAttributes
                var selectAttrs = type.GetCustomAttributes<MqttSelectAttribute>();
                foreach (var attr in selectAttrs)
                {
                    await RegisterSelectAsync(type, attr.Id, uniqueId, deviceInfo);
                }
            }
        }

        private async Task RegisterButtonAsync(Type featureType, string id, string uniqueId, object deviceInfo)
        {
            var btnConfig = new
            {
                name = id,
                unique_id = $"{uniqueId}_btn_{id}",
                object_id = $"{uniqueId}_{id}",
                command_topic = $"homeassistant/button/{uniqueId}/{id}/set",
                device = deviceInfo,
                icon = "mdi:gesture-tap-button"
            };

            _logger.LogInformation("Binding MQTT button '{Id}' to feature class '{ClassName}' at topic 'homeassistant/button/{uniqueId}/{id}/set'", id, featureType.Name, uniqueId, id);

            await _mqttManager.EnqueueAsync($"homeassistant/button/{uniqueId}/{id}/config", JsonSerializer.Serialize(btnConfig), true);
            await _mqttManager.SubscribeAsync($"homeassistant/button/{uniqueId}/{id}/set", async (payload) =>
            {
                _logger.LogInformation("Executing MQTT button '{Id}' for feature '{ClassName}'", id, featureType.Name);
                await ExecuteFeatureAsync(featureType);
            });
        }

        private async Task RegisterSwitchAsync(Type featureType, string id, string uniqueId, object deviceInfo)
        {
            var swConfig = new
            {
                name = id,
                unique_id = $"{uniqueId}_sw_{id}",
                object_id = $"{uniqueId}_{id}",
                command_topic = $"homeassistant/switch/{uniqueId}/{id}/set",
                state_topic = $"homeassistant/switch/{uniqueId}/{id}/state",
                device = deviceInfo,
                icon = "mdi:toggle-switch"
            };

            _logger.LogInformation("Binding MQTT switch '{Id}' to feature class '{ClassName}'", id, featureType.Name);

            await _mqttManager.EnqueueAsync($"homeassistant/switch/{uniqueId}/{id}/config", JsonSerializer.Serialize(swConfig), true);
            await _mqttManager.SubscribeAsync($"homeassistant/switch/{uniqueId}/{id}/set", async (payload) =>
            {
                _logger.LogInformation("Executing MQTT switch '{Id}' for feature '{ClassName}' with payload '{Payload}'", id, featureType.Name, payload);
                // Simple execute
                await ExecuteFeatureAsync(featureType);
            });
        }

        private async Task RegisterSelectAsync(Type featureType, string id, string uniqueId, object deviceInfo)
        {
            var selConfig = new
            {
                name = id,
                unique_id = $"{uniqueId}_sel_{id}",
                object_id = $"{uniqueId}_{id}",
                command_topic = $"homeassistant/select/{uniqueId}/{id}/set",
                state_topic = $"homeassistant/select/{uniqueId}/{id}/state",
                device = deviceInfo,
                icon = "mdi:format-list-bulleted"
            };

            _logger.LogInformation("Binding MQTT select '{Id}' to feature class '{ClassName}'", id, featureType.Name);

            await _mqttManager.EnqueueAsync($"homeassistant/select/{uniqueId}/{id}/config", JsonSerializer.Serialize(selConfig), true);
            await _mqttManager.SubscribeAsync($"homeassistant/select/{uniqueId}/{id}/set", async (payload) =>
            {
                _logger.LogInformation("Executing MQTT select '{Id}' for feature '{ClassName}' with option '{Payload}'", id, featureType.Name, payload);
                await ExecuteFeatureAsync(featureType);
            });
        }

        private async Task ExecuteFeatureAsync(Type featureType)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var feature = scope.ServiceProvider.GetRequiredService(featureType);
                
                // Use reflection to find the ExecuteAsync method
                var executeMethod = featureType.GetMethod("ExecuteAsync");
                if (executeMethod == null)
                {
                    _logger.LogError("ExecuteAsync method not found on feature type '{TypeName}'", featureType.FullName);
                    return;
                }

                var parameters = executeMethod.GetParameters();
                if (parameters.Length == 0) return;

                var requestType = parameters[0].ParameterType;
                
                // Create a default instance of the TRequest record/class
                object requestInstance;
                try
                {
                    requestInstance = Activator.CreateInstance(requestType) 
                                      ?? throw new InvalidOperationException($"Could not instantiate default request of type {requestType.Name}");
                }
                catch
                {
                    // Fallback to parameterized constructor with default values if constructor requires arguments (like records with primary constructor)
                    var constructor = requestType.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
                    if (constructor != null)
                    {
                        var ctorParams = constructor.GetParameters();
                        var args = ctorParams.Select(p => p.HasDefaultValue ? p.DefaultValue : GetDefaultValue(p.ParameterType)).ToArray();
                        requestInstance = constructor.Invoke(args);
                    }
                    else
                    {
                        throw;
                    }
                }

                var context = new WinAgent.Common.Features.ExecutionContext(ExecutionSource.Mqtt);
                var task = executeMethod.Invoke(feature, new object[] { requestInstance, scope.ServiceProvider, context }) as Task;
                if (task != null)
                {
                    await task;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute feature '{TypeName}' via MQTT", featureType.FullName);
            }
        }

        private static object? GetDefaultValue(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
