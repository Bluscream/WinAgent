using System.Diagnostics;

namespace WinAgent.Services;

public class UpdateService
{
    private readonly ServiceService _serviceService;

    public UpdateService(ServiceService serviceService)
    {
        _serviceService = serviceService;
    }

    public string Update(bool install = false, bool rebootIfNeeded = false)
    {
        try
        {
            // 1. Restart Windows Update service (wuauserv)
            string stopResult;
            try 
            {
                stopResult = _serviceService.StopService("wuauserv");
            }
            catch (Exception ex)
            {
                stopResult = $"Stop failed: {ex.Message}";
            }

            var startResult = _serviceService.StartService("wuauserv");

            // 2. Trigger updates
            if (install)
            {
                RunUsoClient("StartDownload");
                RunUsoClient("StartInstall");
                if (rebootIfNeeded) RunUsoClient("RestartDevice");
                return $"Windows Update service handled ({stopResult}, {startResult}). Update download and installation triggered.";
            }

            RunUsoClient("StartScan");
            return $"Windows Update service handled ({stopResult}, {startResult}). Update scan triggered.";
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to trigger Windows Update: {ex.Message}");
        }
    }

    private void RunUsoClient(string argument)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "UsoClient.exe",
            Arguments = argument,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
