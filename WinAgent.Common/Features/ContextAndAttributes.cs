using System;

namespace WinAgent.Common.Features;

public enum ExecutionSource
{
    Http,
    Mcp,
    Mqtt,
    Cli,
    Ipc,
    Unknown
}

public class ExecutionContext
{
    public ExecutionSource Source { get; }
    
    public ExecutionContext(ExecutionSource source)
    {
        Source = source;
    }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public class MqttButtonAttribute : Attribute
{
    public string Id { get; }
    public MqttButtonAttribute(string id) => Id = id;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public class MqttSwitchAttribute : Attribute
{
    public string Id { get; }
    public MqttSwitchAttribute(string id) => Id = id;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public class MqttSelectAttribute : Attribute
{
    public string Id { get; }
    public MqttSelectAttribute(string id) => Id = id;
}
