using System;
using System.Diagnostics;
using WinAgent.Utils;
using WinAgent.Models;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace WinAgent;

public class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon _notifyIcon = null!;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _token;
    private static readonly string Shell32Path = Path.Combine(Environment.SystemDirectory, "shell32.dll");
    private static readonly string PowerCplPath = Path.Combine(Environment.SystemDirectory, "powercpl.dll");

    private HiddenMessageWindow _messageWindow;
    private System.Windows.Forms.Timer _serviceMonitorTimer = null!;
    private bool _hasPromptedServiceDown = false;

    public bool IsBlockShutdownEnabled { get; set; }

    public TrayApplicationContext()
    {
        _token = Config.Get("token", "-token", "WINAGENT_TOKEN") ?? string.Empty;
        if (string.IsNullOrEmpty(_token))
        {
            MessageBox.Show("No WINAGENT_TOKEN configured.\n\nSet the WINAGENT_TOKEN environment variable or add it to appsettings.json.", "WinAgent Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        var portStr = Config.Get("port", "WINAGENT_PORT") ?? "23482";
        _baseUrl = $"http://localhost:{portStr}";
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        _messageWindow = new HiddenMessageWindow(this);
        _messageWindow.Show();

        _serviceMonitorTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _serviceMonitorTimer.Tick += ServiceMonitorTimer_Tick;
        _serviceMonitorTimer.Start();

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Icon trayIcon;
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                trayIcon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
            }
            else
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                trayIcon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "WinAgent",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();

        // Block Shutdown Toggle
        var blockShutdownItem = new ToolStripMenuItem("Block Shutdown", null, async (s, e) => await ToggleBlockShutdown((ToolStripMenuItem)s!));
        blockShutdownItem.CheckOnClick = false; // Manual handling
        blockShutdownItem.Image = SystemIcons.Shield.ToBitmap();
        
        // Force Action Toggle
        var forceActionItem = new ToolStripMenuItem("Force Action", null, async (s, e) => await ToggleForceAction((ToolStripMenuItem)s!));
        forceActionItem.CheckOnClick = false; // Manual handling
        forceActionItem.Image = SystemIcons.Warning.ToBitmap();

        // Service Running Toggle
        var serviceToggleItem = new ToolStripMenuItem("Service Running", null, (s, e) => ToggleService((ToolStripMenuItem)s!));
        serviceToggleItem.CheckOnClick = false; // Manual handling
        serviceToggleItem.ToolTipText = "Start or Stop the background WinAgent service (Requires Admin)";
        serviceToggleItem.Image = SystemIcons.Shield.ToBitmap();

        // Persistence Setup
        var setupPersistenceItem = new ToolStripMenuItem("Setup Persistence", null, (s, e) => {
            try {
                var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinAgent.Service.exe");
                if (!File.Exists(exePath)) exePath = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'))!, "WinAgent.Service", "WinAgent.Service.exe");
                var psi = new ProcessStartInfo(exePath, "--install") { Verb = "runas", UseShellExecute = true };
                Process.Start(psi);
                MessageBox.Show("Persistence setup launched. Check the service console for results.", "WinAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex) {
                MessageBox.Show($"Failed to launch setup: {ex.Message}", "WinAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
        setupPersistenceItem.Image = GetSystemIcon(Shell32Path, 219); // Gear

        // 1. Power Options Submenu (contains shutdown, reboot, lock, logoff, plus block shutdown and force action)
        var powerMenu = new ToolStripMenuItem("Power / Shutdown");
        powerMenu.Image = GetSystemIcon(Shell32Path, 27); // Power logo

        var lockItem = new ToolStripMenuItem("Lock Workstation", null, (s, e) => ExecuteAction("lock"));
        lockItem.Image = GetSystemIcon(Shell32Path, 47); // Padlock

        var logoffItem = new ToolStripMenuItem("Logoff User", null, (s, e) => ExecuteAction("logoff"));
        logoffItem.Image = GetSystemIcon(Shell32Path, 45); // User key / Arrow

        var rebootItem = new ToolStripMenuItem("Reboot System", null, (s, e) => ExecuteAction("reboot"));
        rebootItem.Image = GetSystemIcon(Shell32Path, 238); // Circular green arrow

        var shutdownItem = new ToolStripMenuItem("Shutdown System", null, (s, e) => ExecuteAction("shutdown"));
        shutdownItem.Image = GetSystemIcon(Shell32Path, 27); // Red power off

        powerMenu.DropDownItems.Add(lockItem);
        powerMenu.DropDownItems.Add(logoffItem);
        powerMenu.DropDownItems.Add(rebootItem);
        powerMenu.DropDownItems.Add(shutdownItem);
        powerMenu.DropDownItems.Add(new ToolStripSeparator());
        powerMenu.DropDownItems.Add(blockShutdownItem);
        powerMenu.DropDownItems.Add(forceActionItem);

        // Fetch Block/Force status on opening the Power Options submenu
        powerMenu.DropDownOpening += async (s, e) => {
            try {
                var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/block-status");
                if (resp.IsSuccessStatusCode) {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var data = System.Text.Json.JsonDocument.Parse(json);
                    blockShutdownItem.Checked = data.RootElement.GetProperty("enabled").GetBoolean();
                }
            } catch { }
            try {
                var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/force-status");
                if (resp.IsSuccessStatusCode) {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var data = System.Text.Json.JsonDocument.Parse(json);
                    forceActionItem.Checked = data.RootElement.GetProperty("enabled").GetBoolean();
                }
            } catch { }
        };

        // 2. Power Profiles Submenu
        var powerProfilesMenu = new ToolStripMenuItem("Power Profiles");
        powerProfilesMenu.Image = GetSystemIcon(PowerCplPath, 0); // Battery with plug
        powerProfilesMenu.DropDownOpening += async (s, e) => await PopulatePowerProfilesMenu(powerProfilesMenu);

        // 3. Devices Submenu
        var devicesMenu = new ToolStripMenuItem("Devices");
        devicesMenu.Image = GetSystemIcon(Shell32Path, 172); // Hardware/Device Manager
        devicesMenu.DropDownOpening += async (s, e) => await PopulateDevicesMenu(devicesMenu);

        // Add to main context menu
        contextMenu.Items.Add(serviceToggleItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(powerMenu);
        contextMenu.Items.Add(powerProfilesMenu);
        contextMenu.Items.Add(devicesMenu);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(setupPersistenceItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit", null, ExitApplication);
        exitItem.Image = GetSystemIcon(Shell32Path, 131); // Red cross / Exit
        contextMenu.Items.Add(exitItem);

        contextMenu.Opening += (s, e) => {
            // Update Service Status (Admin required for some actions, but status is readable)
            serviceToggleItem.Checked = ServiceHelper.IsServiceRunning("WinAgent");
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
            serviceToggleItem.Enabled = isAdmin;
        };

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private async Task ToggleBlockShutdown(ToolStripMenuItem item)
    {
        bool newState = !item.Checked;
        try {
            await _httpClient.PostAsync($"{_baseUrl}/api/system/toggle-block?enabled={newState}", null);
            item.Checked = newState;
        } catch (Exception ex) {
            MessageBox.Show($"Failed to communicate with service: {ex.Message}");
        }
    }

    private void ToggleService(ToolStripMenuItem item)
    {
        try {
            bool isRunning = ServiceHelper.IsServiceRunning("WinAgent");
            if (isRunning) {
                ServiceHelper.StopService("WinAgent");
                item.Checked = false;
            } else {
                ServiceHelper.StartService("WinAgent");
                item.Checked = true;
            }
        } catch (Exception ex) {
            MessageBox.Show($"Failed to toggle service: {ex.Message}\n\nMake sure the application is running as Administrator.", "WinAgent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ToggleForceAction(ToolStripMenuItem item)
    {
        bool newState = !item.Checked;
        try {
            await _httpClient.PostAsync($"{_baseUrl}/api/system/toggle-force?enabled={newState}", null);
            item.Checked = newState;
        } catch (Exception ex) {
            MessageBox.Show($"Failed to communicate with service: {ex.Message}");
        }
    }

    private async void ExecuteAction(string action)
    {
        try {
            await _httpClient.PostAsync($"{_baseUrl}/api/system/execute?action={action}", null);
        } catch (Exception ex) {
            MessageBox.Show($"Failed to execute action via IPC: {ex.Message}");
        }
    }

    private void ExitApplication(object? sender, EventArgs e)
    {
        _serviceMonitorTimer?.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }

    private async void ServiceMonitorTimer_Tick(object? sender, EventArgs e)
    {
        bool serviceRunning = ServiceHelper.IsServiceRunning("WinAgent");
        if (!serviceRunning)
        {
            IsBlockShutdownEnabled = false;
            if (!_hasPromptedServiceDown)
            {
                _hasPromptedServiceDown = true;
                var res = MessageBox.Show(
                    "The WinAgent Windows Service has stopped or crashed. Would you like to restart it now?\n\n(The tray app relies on the service to function properly.)", 
                    "WinAgent Service Stopped", 
                    MessageBoxButtons.YesNo, 
                    MessageBoxIcon.Warning);

                if (res == DialogResult.Yes)
                {
                    try {
                        ServiceHelper.StartService("WinAgent");
                        _hasPromptedServiceDown = false;
                        MessageBox.Show("Service start requested.", "WinAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    } catch (Exception ex) {
                        MessageBox.Show($"Failed to start service: {ex.Message}\nTry running the tray as Administrator.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        else
        {
            _hasPromptedServiceDown = false;
            // Fetch and cache the block status from the service
            try
            {
                var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/block-status");
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    IsBlockShutdownEnabled = doc.RootElement.GetProperty("enabled").GetBoolean();
                }
            }
            catch { }
        }
    }

    private class PowerProfileDto
    {
        public string Name { get; set; } = "";
        public string Guid { get; set; } = "";
        public bool IsActive { get; set; }
    }

    private class DeviceDto
    {
        public string Name { get; set; } = "";
        public string DeviceID { get; set; } = "";
        public string Class { get; set; } = "";
        public string ClassGuid { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Present { get; set; }
        public bool Enabled { get; set; }
    }

    private bool _isPopulatingPower = false;
    private async Task PopulatePowerProfilesMenu(ToolStripMenuItem powerMenu)
    {
        if (_isPopulatingPower) return;
        _isPopulatingPower = true;

        powerMenu.DropDownItems.Clear();
        var loadingItem = new ToolStripMenuItem("Loading...") { Enabled = false };
        powerMenu.DropDownItems.Add(loadingItem);

        try
        {
            var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/power-schemes");
            if (!resp.IsSuccessStatusCode)
            {
                loadingItem.Text = "Failed to load power profiles.";
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var profiles = System.Text.Json.JsonSerializer.Deserialize<List<PowerProfileDto>>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (profiles == null || profiles.Count == 0)
            {
                loadingItem.Text = "No power profiles found.";
                return;
            }

            powerMenu.DropDownItems.Clear();

            foreach (var prof in profiles)
            {
                var item = new ToolStripMenuItem(prof.Name);
                item.Checked = prof.IsActive;
                item.CheckOnClick = false;
                item.Image = GetPowerProfileIcon(prof.Name, prof.Guid);

                string profileName = prof.Name;
                item.Click += async (s, ev) =>
                {
                    var clickedItem = (ToolStripMenuItem)s!;
                    clickedItem.Enabled = false;
                    try
                    {
                        var setResp = await _httpClient.PostAsync($"{_baseUrl}/api/system/set-power-scheme?scheme={Uri.EscapeDataString(profileName)}", null);
                        if (setResp.IsSuccessStatusCode)
                        {
                            // Uncheck all other items and check this one
                            foreach (ToolStripItem other in powerMenu.DropDownItems)
                            {
                                if (other is ToolStripMenuItem mi) mi.Checked = false;
                            }
                            clickedItem.Checked = true;
                        }
                        else
                        {
                            MessageBox.Show("Failed to set power profile.", "Power Profile Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to communicate with service: {ex.Message}", "IPC Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        clickedItem.Enabled = true;
                    }
                };

                powerMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            powerMenu.DropDownItems.Clear();
            powerMenu.DropDownItems.Add(new ToolStripMenuItem($"Error: {ex.Message}") { Enabled = false });
        }
        finally
        {
            _isPopulatingPower = false;
        }
    }

    private bool _isPopulatingDevices = false;
    private async Task PopulateDevicesMenu(ToolStripMenuItem devicesMenuItem)
    {
        if (_isPopulatingDevices) return;
        _isPopulatingDevices = true;

        devicesMenuItem.DropDownItems.Clear();
        var loadingItem = new ToolStripMenuItem("Loading...") { Enabled = false };
        devicesMenuItem.DropDownItems.Add(loadingItem);

        try
        {
            var resp = await _httpClient.GetAsync($"{_baseUrl}/api/device-list");
            if (!resp.IsSuccessStatusCode)
            {
                loadingItem.Text = "Failed to load devices.";
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var devices = System.Text.Json.JsonSerializer.Deserialize<List<DeviceDto>>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (devices == null || devices.Count == 0)
            {
                loadingItem.Text = "No devices found.";
                return;
            }

            devicesMenuItem.DropDownItems.Clear();

            // Group by Friendly Class name
            var grouped = devices
                .GroupBy(d => GetFriendlyClassName(d.Class))
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                var classMenu = new ToolStripMenuItem(group.Key);
                classMenu.Image = GetFriendlyClassIcon(group.First().Class, group.First().ClassGuid);
                
                // Sort devices alphabetically by Name
                var sortedDevices = group.OrderBy(d => d.Name);

                foreach (var dev in sortedDevices)
                {
                    // Clean up name if too long
                    string displayName = dev.Name;
                    if (displayName.Length > 60) displayName = displayName.Substring(0, 57) + "...";

                    var devItem = new ToolStripMenuItem(displayName);
                    devItem.Checked = dev.Enabled;
                    devItem.CheckOnClick = false;
                    
                    // Bind toggle action
                    string devId = dev.DeviceID;
                    devItem.Click += async (s, ev) =>
                    {
                        var clickedItem = (ToolStripMenuItem)s!;
                        bool targetState = !clickedItem.Checked;
                        string endpoint = targetState ? "device-enable" : "device-disable";
                        
                        clickedItem.Enabled = false; // Disable temporarily during API call
                        try
                        {
                            var toggleResp = await _httpClient.PostAsync($"{_baseUrl}/api/{endpoint}?pattern={Uri.EscapeDataString(devId)}", null);
                            if (toggleResp.IsSuccessStatusCode)
                            {
                                var contentJson = await toggleResp.Content.ReadAsStringAsync();
                                var result = System.Text.Json.JsonSerializer.Deserialize<DeviceToggleResult>(contentJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (result != null)
                                {
                                    if (result.Success)
                                    {
                                        clickedItem.Checked = targetState;
                                        
                                        var detail = result.Results.FirstOrDefault();
                                        string heading = targetState ? "Device Enabled Successfully" : "Device Disabled Successfully";
                                        string msg = detail != null ? detail.Message : $"Device toggled successfully.";
                                        string details = detail != null ? $"Device Name: {detail.Name}\nDevice ID: {detail.DeviceID}\nAction: {detail.Action}\nStatus: Succeeded" : "";
                                        
                                        var thread = new Thread(() =>
                                        {
                                            Modern_Windows_Message_Box_Generator.CLI.Program.Main(new[] { 
                                                "--messagebox", 
                                                "--title", "MQTT Agent", 
                                                "--heading", heading, 
                                                "--message", msg, 
                                                "--icon", "shieldgreen",
                                                "--details", details
                                            }).Wait();
                                        });
                                        thread.SetApartmentState(ApartmentState.STA);
                                        thread.Start();
                                    }
                                    else
                                    {
                                        var detail = result.Results.FirstOrDefault();
                                        string heading = "Device Action Failed";
                                        string msg = detail != null ? detail.Message : "Failed to toggle device.";
                                        string error = detail?.Error ?? "Unknown error.";
                                        string details = detail != null ? $"Device Name: {detail.Name}\nDevice ID: {detail.DeviceID}\nAction: {detail.Action}\nStatus: Failed\nError: {error}" : "";
                                        
                                        var thread = new Thread(() =>
                                        {
                                            Modern_Windows_Message_Box_Generator.CLI.Program.Main(new[] { 
                                                "--messagebox", 
                                                "--title", "MQTT Agent", 
                                                "--heading", heading, 
                                                "--message", msg, 
                                                "--icon", "shieldred",
                                                "--details", details
                                            }).Wait();
                                        });
                                        thread.SetApartmentState(ApartmentState.STA);
                                        thread.Start();
                                    }
                                }
                                else
                                {
                                    var thread = new Thread(() =>
                                    {
                                        Modern_Windows_Message_Box_Generator.CLI.Program.Main(new[] { 
                                            "--messagebox", 
                                            "--title", "MQTT Agent", 
                                            "--heading", "Device Action Completed", 
                                            "--message", "Device action completed with an empty or unparseable result.", 
                                            "--icon", "shieldyellow",
                                            "--details", $"Raw response:\n{contentJson}"
                                        }).Wait();
                                    });
                                    thread.SetApartmentState(ApartmentState.STA);
                                    thread.Start();
                                }
                            }
                            else
                            {
                                var errText = await toggleResp.Content.ReadAsStringAsync();
                                string detailsStr = $"HTTP Status: {(int)toggleResp.StatusCode} {toggleResp.ReasonPhrase}\nResponse: {errText}";
                                var thread = new Thread(() =>
                                {
                                    Modern_Windows_Message_Box_Generator.CLI.Program.Main(new[] { 
                                        "--messagebox", 
                                        "--title", "MQTT Agent Error", 
                                        "--heading", "Device Toggle Failed", 
                                        "--message", $"The service returned an HTTP error: {toggleResp.StatusCode}", 
                                        "--icon", "shieldred",
                                        "--details", detailsStr
                                    }).Wait();
                                });
                                thread.SetApartmentState(ApartmentState.STA);
                                thread.Start();
                            }
                        }
                        catch (Exception ex)
                        {
                            var thread = new Thread(() =>
                            {
                                Modern_Windows_Message_Box_Generator.CLI.Program.Main(new[] { 
                                    "--messagebox", 
                                    "--title", "MQTT Agent Connection Error", 
                                    "--heading", "IPC Communication Error", 
                                    "--message", $"Failed to communicate with the MQTT Agent service.", 
                                    "--icon", "shieldred",
                                    "--details", $"Error Details: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                                }).Wait();
                            });
                            thread.SetApartmentState(ApartmentState.STA);
                            thread.Start();
                        }
                        finally
                        {
                            clickedItem.Enabled = true;
                        }
                    };

                    classMenu.DropDownItems.Add(devItem);
                }

                devicesMenuItem.DropDownItems.Add(classMenu);
            }
        }
        catch (Exception ex)
        {
            devicesMenuItem.DropDownItems.Clear();
            devicesMenuItem.DropDownItems.Add(new ToolStripMenuItem($"Error: {ex.Message}") { Enabled = false });
        }
        finally
        {
            _isPopulatingDevices = false;
        }
    }

    private static string GetFriendlyClassName(string className)
    {
        if (string.IsNullOrEmpty(className)) return "Other Devices";
        return className switch
        {
            "Net" => "Network adapters",
            "DiskDrive" => "Disk drives",
            "Display" => "Display adapters",
            "Keyboard" => "Keyboards",
            "Mouse" => "Mice and other pointing devices",
            "Monitor" => "Monitors",
            "Media" => "Sound, video and game controllers",
            "USB" => "Universal Serial Bus controllers",
            "System" => "System devices",
            "Bluetooth" => "Bluetooth devices",
            "Camera" => "Cameras",
            "Computer" => "Computer",
            "Image" => "Imaging devices",
            "Ports" => "Ports (COM & LPT)",
            "Processor" => "Processors",
            "Volume" => "Storage volumes",
            "Battery" => "Batteries",
            "SCSIAdapter" => "Storage controllers",
            _ => className
        };
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Image? GetSystemIcon(string file, int index)
    {
        try
        {
            IntPtr largeIcon = IntPtr.Zero;
            IntPtr smallIcon = IntPtr.Zero;
            ExtractIconEx(file, index, out largeIcon, out smallIcon, 1);

            IntPtr chosenIcon = smallIcon != IntPtr.Zero ? smallIcon : largeIcon;
            if (chosenIcon != IntPtr.Zero)
            {
                using var icon = Icon.FromHandle(chosenIcon);
                var bmp = icon.ToBitmap();
                
                if (largeIcon != IntPtr.Zero) DestroyIcon(largeIcon);
                if (smallIcon != IntPtr.Zero) DestroyIcon(smallIcon);
                
                return bmp;
            }
        }
        catch { }
        return null;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiLoadClassIcon(ref Guid classGuid, out IntPtr largeIcon, IntPtr miniIconIndex);

    private static Image? GetFriendlyClassIcon(string className, string classGuidStr)
    {
        // First try SetupDiLoadClassIcon for the exact Windows device manager class icon
        if (!string.IsNullOrEmpty(classGuidStr))
        {
            try
            {
                if (Guid.TryParse(classGuidStr, out Guid guid))
                {
                    IntPtr largeIcon;
                    if (SetupDiLoadClassIcon(ref guid, out largeIcon, IntPtr.Zero))
                    {
                        if (largeIcon != IntPtr.Zero)
                        {
                            using var icon = Icon.FromHandle(largeIcon);
                            var bmp = icon.ToBitmap();
                            DestroyIcon(largeIcon);
                            return bmp;
                        }
                    }
                }
            }
            catch { }
        }

        // Fallback switches if API fails, returning null (no icon) instead of a generic fallback as requested
        int index = className switch
        {
            "Net" => 9,
            "DiskDrive" => 15,
            "Display" => 16,
            "Keyboard" => 258,
            "Mouse" => 259,
            "Monitor" => 16,
            "Media" => 168,
            "USB" => 39,
            "System" => 18,
            "Bluetooth" => 172,
            "Camera" => 261,
            "Computer" => 16,
            "Image" => 261,
            "Ports" => 39,
            "Processor" => 263,
            "Volume" => 11,
            "Battery" => 20,
            "SCSIAdapter" => 15,
            _ => -1
        };

        if (index >= 0)
        {
            return GetSystemIcon(Shell32Path, index);
        }

        return null;
    }

    private static Image? GetPowerProfileIcon(string name, string guidStr)
    {
        string guidLower = guidStr.ToLowerInvariant();

        if (guidLower == "381b4222-f694-41f0-9685-ff5bb260df2e" || name.Contains("balanced", StringComparison.OrdinalIgnoreCase))
        {
            // Balanced: Battery with plug
            return GetSystemIcon(PowerCplPath, 0) ?? GetSystemIcon(Shell32Path, 20);
        }
        else if (guidLower == "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" || name.Contains("high performance", StringComparison.OrdinalIgnoreCase))
        {
            // High Performance: Power plug
            return GetSystemIcon(PowerCplPath, 2) ?? GetSystemIcon(Shell32Path, 27);
        }
        else if (guidLower == "a1841308-3541-4fab-bc81-f71556f20b4a" || name.Contains("saver", StringComparison.OrdinalIgnoreCase))
        {
            // Power Saver: Battery cylinder
            return GetSystemIcon(PowerCplPath, 1) ?? GetSystemIcon(Shell32Path, 20);
        }
        else if (guidLower == "e9a42b02-d5df-448d-aa00-03f14749eb61" || name.Contains("ultimate", StringComparison.OrdinalIgnoreCase))
        {
            // Ultimate Performance: Power plug
            return GetSystemIcon(PowerCplPath, 2) ?? GetSystemIcon(Shell32Path, 27);
        }

        // Generic fallback: Battery cylinder
        return GetSystemIcon(PowerCplPath, 1) ?? GetSystemIcon(Shell32Path, 20);
    }
}

public class HiddenMessageWindow : Form
{
    private readonly TrayApplicationContext _context;
    private readonly int _msgShellHook;
    private const int HSHELL_FLASH = 0x8006;
    private const int HSHELL_WINDOWACTIVATED = 4;
    private const int HSHELL_RUDEAPPACTIVATED = 32772;

    private const int WM_QUERYENDSESSION = 0x11;
    private const int WM_ENDSESSION = 0x16;
    private const uint ENDSESSION_LOGOFF = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool AbortSystemShutdown(string? lpMachineName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

    public HiddenMessageWindow(TrayApplicationContext context)
    {
        _context = context;
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        
        // Set process priority for shutdown/logoff to highest possible (0x4FF)
        try { SetProcessShutdownParameters(0x4FF, 0); } catch { }

        this.Load += (s, e) =>
        {
            this.Size = new Size(0, 0);
            NativeMethods.RegisterShellHookWindow(this.Handle);
        };
        _msgShellHook = NativeMethods.RegisterWindowMessage("SHELLHOOK");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
        {
            bool isLogoff = (m.LParam.ToInt64() & ENDSESSION_LOGOFF) != 0;
            string action = isLogoff ? "Logoff" : "Shutdown";

            if (_context.IsBlockShutdownEnabled)
            {
                ShutdownBlockReasonCreate(this.Handle, $"WinAgent has blocked {action} via Home Assistant.");
                m.Result = IntPtr.Zero; // 0 = block, 1 = allow
                
                try
                {
                    AbortSystemShutdown(null);
                }
                catch { }
                return;
            }
            else
            {
                ShutdownBlockReasonDestroy(this.Handle);
            }
        }
        else if (m.Msg == _msgShellHook)
        {
            int wParam = m.WParam.ToInt32();
            if (wParam == HSHELL_FLASH)
            {
                SystemHelper.FlashingWindows.Add(m.LParam);
            }
            else if (wParam == HSHELL_WINDOWACTIVATED || wParam == HSHELL_RUDEAPPACTIVATED)
            {
                SystemHelper.FlashingWindows.Remove(m.LParam);
            }
        }
        base.WndProc(ref m);
    }
}
