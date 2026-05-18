using System;
using System.Windows.Forms;
using WinAgent.Utils;
using Serilog;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace WinAgent;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);

        // Minimal config initialization for the tray
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(baseDir, "WinAgent.json"), optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        Config.Initialize(config);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WinAgent Tray crashed: {ex.Message}", "WinAgent Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}