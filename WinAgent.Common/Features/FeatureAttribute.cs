using System;

namespace WinAgent.Common.Features;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class FeatureAttribute : Attribute
{
    /// <summary>
    /// The path used for API endpoints, MCP tool names, and MQTT routing.
    /// E.g. "capture/screenshot". Slashes are allowed.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The description of the feature to display in Swagger and MCP tools.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
