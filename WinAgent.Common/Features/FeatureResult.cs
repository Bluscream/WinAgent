using System;

namespace WinAgent.Common.Features;

public class FeatureResult
{
    public object? JsonData { get; set; }
    public byte[]? FileBytes { get; set; }
    public string? FileMimeType { get; set; }
    public string? FileName { get; set; }
    public string? PlainText { get; set; }

    public static FeatureResult FromJson(object data) => new FeatureResult { JsonData = data };
    public static FeatureResult FromFile(byte[] bytes, string mimeType, string? fileName = null) => new FeatureResult { FileBytes = bytes, FileMimeType = mimeType, FileName = fileName };
    public static FeatureResult FromText(string text) => new FeatureResult { PlainText = text };
}
