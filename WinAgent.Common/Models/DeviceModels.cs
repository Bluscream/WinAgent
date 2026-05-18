using System.Collections.Generic;

namespace WinAgent.Models;

public class DeviceToggleDetail
{
    public string Name { get; set; } = "";
    public string DeviceID { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Error { get; set; }
}

public class DeviceToggleResult
{
    public bool Success { get; set; }
    public List<DeviceToggleDetail> Results { get; set; } = new();
}

public class DeviceInfo
{
    public string Name { get; set; } = "";
    public string DeviceID { get; set; } = "";
    public string Class { get; set; } = "";
    public string ClassGuid { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Present { get; set; }
    public bool Enabled { get; set; }
    public string? Icon { get; set; }
}
