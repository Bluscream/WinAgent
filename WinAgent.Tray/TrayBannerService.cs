using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SoundSwitch.Banner;

namespace WinAgent;

/// <summary>
/// Manages a single shared <see cref="BannerService"/> instance running on a
/// dedicated, persistent STA thread. All banner requests are marshalled onto
/// that thread via its <see cref="SynchronizationContext"/>, so the
/// BannerService queue and MaxConcurrentBanners limit work correctly across
/// multiple concurrent requests.
/// </summary>
public static class TrayBannerService
{
    // Default configuration – TTL is overridden per-request via BannerRequest.Ttl
    private sealed class Config : IBannerConfiguration
    {
        public int Opacity { get; set; } = 100;
        public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxConcurrentBanners => 5;
        public ShowOnScreen ShowOn => ShowOnScreen.PrimaryScreen;
        public Point? CustomPosition => null;
    }

    private static Thread? _uiThread;
    private static SynchronizationContext? _uiContext;
    private static BannerService? _bannerService;
    private static ApplicationContext? _appContext;

    private static readonly ManualResetEventSlim _ready = new(false);
    private static readonly object _lock = new();

    public static bool IsReady => _ready.IsSet;

    /// <summary>
    /// Starts the dedicated STA UI thread. Safe to call multiple times.
    /// </summary>
    public static void Start()
    {
        lock (_lock)
        {
            if (_uiThread != null && _uiThread.IsAlive) return;

            _ready.Reset();

            _uiThread = new Thread(() =>
            {
                ApplicationConfiguration.Initialize();

                // WindowsFormsSynchronizationContext is installed by ApplicationConfiguration.Initialize()
                // or by creating the first Form – capture it now.
                _appContext = new ApplicationContext();

                // We need a dummy form to ensure the WinForms infrastructure is set up
                // before BannerService.Setup() captures the SynchronizationContext.
                using var bootstrapForm = new Form
                {
                    Opacity = 0,
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    Size = new System.Drawing.Size(1, 1),
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-32000, -32000)
                };
                bootstrapForm.Show();
                bootstrapForm.Hide();

                // Ensure the synchronization context is a WindowsFormsSynchronizationContext
                if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                BannerService.Setup();
                _uiContext = SynchronizationContext.Current;
                _bannerService = new BannerService(new Config());

                _ready.Set();

                Application.Run(_appContext);

                // Cleanup after the message loop exits
                _uiContext = null;
                _bannerService = null;
                _ready.Reset();
            });

            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Name = "TrayBannerUIThread";
            _uiThread.Start();
        }
    }

    /// <summary>
    /// Stops the dedicated STA UI thread gracefully.
    /// </summary>
    public static void Stop()
    {
        lock (_lock)
        {
            var ctx = _appContext;
            var syncCtx = _uiContext;
            if (ctx != null && syncCtx != null)
            {
                syncCtx.Post(_ => ctx.ExitThread(), null);
            }
        }
    }

    /// <summary>
    /// Shows a banner on the shared UI thread. Returns immediately; the banner
    /// is displayed asynchronously. If the service is not ready, waits up to
    /// <paramref name="startupTimeoutMs"/> for it to become available.
    /// </summary>
    public static async Task ShowAsync(
        string title,
        string message,
        string position = "TopLeft",
        string? imagePath = null,
        int durationSeconds = 3,
        string? callback = null,
        string? priority = null,
        bool ding = false,
        int startupTimeoutMs = 3000)
    {
        // Ensure the thread is running
        Start();

        // Wait for the thread to be ready
        if (!_ready.Wait(startupTimeoutMs))
            throw new TimeoutException("TrayBannerService UI thread did not become ready in time.");

        var syncCtx = _uiContext;
        var service = _bannerService;
        if (syncCtx == null || service == null)
            throw new InvalidOperationException("TrayBannerService is not initialized.");

        if (!Enum.TryParse<BannerPosition>(position, ignoreCase: true, out var pos))
            pos = BannerPosition.TopLeft;

        // Play notification sound before showing the banner
        if (ding)
        {
            try { System.Media.SystemSounds.Exclamation.Play(); }
            catch { /* swallow */ }
        }

        // Resolve image (may involve async I/O – do it outside the UI thread)
        Image? resolvedImage = null;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            try { resolvedImage = await ImageResolver.ResolveAsync(imagePath); }
            catch { /* swallow – image is optional */ }
        }

        // Map priority string to int (higher = more important)
        int priorityInt = MapPriority(priority);

        // Build the OnClick handler for callback URL
        EventHandler? onClick = null;
        if (!string.IsNullOrWhiteSpace(callback))
        {
            onClick = (sender, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = callback,
                        UseShellExecute = true
                    });
                }
                catch { /* swallow – best effort */ }
                // Dispose the banner form after click
                if (sender is IDisposable d) d.Dispose();
            };
        }

        var request = new BannerRequest
        {
            Title = title,
            Text = message,
            Image = resolvedImage,
            Priority = priorityInt,
            Ttl = TimeSpan.FromSeconds(durationSeconds > 0 ? durationSeconds : 3),
            OnClick = onClick
        };

        // Marshal to the STA UI thread – Show() uses _syncContext.Send() internally,
        // so we just need to be on the right thread.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        syncCtx.Post(_ =>
        {
            try
            {
                service.Show(request, pos);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);

        await tcs.Task;
    }

    private static int MapPriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return 1;
        return priority.ToLowerInvariant() switch
        {
            "min" => -2,
            "low" => -1,
            "default" => 0,
            "high" => 1,
            "max" => 2,
            _ => int.TryParse(priority, out var v) ? v : 1
        };
    }
}
