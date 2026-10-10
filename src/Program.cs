using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WallpaperCycle
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex instance = new Mutex(true, @"Local\WallpaperCycle", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                try
                {
                    Settings settings = Settings.Load(root);
                    using (TrayApplication app = new TrayApplication(root, settings)) Application.Run(app);
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message + "\n\nCheck config.jsonc in " + root, "WallpaperCycle could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { instance.ReleaseMutex(); }
            }
        }
    }

    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly string root;
        private readonly NotifyIcon icon;
        private readonly Icon trayIcon;
        private readonly System.Windows.Forms.Timer timer;
        private readonly ToolStripMenuItem next;
        private readonly ToolStripMenuItem status;
        private readonly ToolStripMenuItem exit;
        private readonly ToolStripMenuItem favorite;
        private readonly HttpClient http;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Rotation rotation;
        private readonly RotationSchedule schedule;
        private readonly AppLog log;
        private Settings settings;
        private DateTime configLastWriteUtc;
        private DateTime rejectedConfigWriteUtc = DateTime.MinValue;
        private bool busy;
        private bool closing;
        private bool disposed;
        private bool backgroundModeInitialized;
        private bool lastBlackMode;
        private DateTime? scheduledBlackSuppressedUntil;

        internal bool IsClosing { get { return closing; } }
        internal bool IsTrayIconVisible { get { return icon.Visible; } }
        internal ToolStripMenuItem ExitMenuItem { get { return exit; } }
        internal ToolStripMenuItem FavoriteMenuItem { get { return favorite; } }
        internal ToolStripMenuItem BlackBackgroundMenuItem { get; private set; }
        internal Settings CurrentSettings { get { return settings; } }

        public TrayApplication(string root, Settings settings)
        {
            this.root = root;
            this.settings = settings;
            configLastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(root, "config.jsonc"));
            trayIcon = TrayIcon.Create();
            log = new AppLog(root);
            http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WallpaperCycle/1.0");
            schedule = new RotationSchedule(settings.IntervalMinutes, DateTime.UtcNow);

            ContextMenuStrip menu = new ContextMenuStrip();
            status = new ToolStripMenuItem("Starting...") { Enabled = false };
            next = new ToolStripMenuItem("Next", null, async delegate { await RequestNextWallpaperAsync(); });
            favorite = new ToolStripMenuItem("Favorite", null, delegate { FavoriteCurrentWallpaper(); });
            ToolStripMenuItem pause = new ToolStripMenuItem("Pause") { CheckOnClick = true };
            pause.CheckedChanged += delegate { schedule.Paused = pause.Checked; };
            ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows") { Checked = WindowsIntegration.StartsWithWindows(Application.ExecutablePath) };
            startup.Click += delegate {
                try
                {
                    WindowsIntegration.SetStartsWithWindows(Application.ExecutablePath, !startup.Checked);
                    startup.Checked = !startup.Checked;
                }
                catch (Exception error) { Notify("Startup setting failed", error.Message); }
            };
            BlackBackgroundMenuItem = new ToolStripMenuItem("Black") { CheckOnClick = true, Checked = settings.BlackBackground };
            BlackBackgroundMenuItem.Click += async delegate {
                try
                {
                    Settings updated = Settings.Load(root);
                    updated.BlackBackground = BlackBackgroundMenuItem.Checked;
                    updated.Save(root);
                    ApplySettings(updated);
                    await RotateAsync();
                }
                catch (Exception error)
                {
                    BlackBackgroundMenuItem.Checked = settings.BlackBackground;
                    Notify("Could not update background setting", error.Message);
                }
            };
            exit = new ToolStripMenuItem("Exit", null, delegate { RequestExit(); });
            menu.Items.AddRange(new ToolStripItem[] {
                status, new ToolStripSeparator(), next, favorite, pause, BlackBackgroundMenuItem,
                new ToolStripMenuItem("Config", null, delegate { Open("notepad.exe", Path.Combine(root, "config.jsonc")); }),
                new ToolStripMenuItem("Wallpapers", null, delegate { Open("explorer.exe", Path.Combine(root, "wallpapers")); }),
                new ToolStripMenuItem("Favorites", null, delegate { Open("explorer.exe", Path.Combine(root, "favorites")); }),
                new ToolStripMenuItem("Log", null, delegate { Open("notepad.exe", Path.Combine(root, "data", "app.log")); }),
                startup, new ToolStripSeparator(), exit
            });
            icon = new NotifyIcon { Icon = trayIcon, Text = "WallpaperCycle", ContextMenuStrip = menu, Visible = true };
            icon.DoubleClick += async delegate { await RequestNextWallpaperAsync(); };
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += async delegate {
                if (busy || closing) return;
                bool configChanged = ReloadConfigIfChanged();
                bool blackNow = UsesBlackBackgroundNow(DateTime.Now);
                if (!backgroundModeInitialized || blackNow != lastBlackMode || configChanged)
                {
                    backgroundModeInitialized = true;
                    lastBlackMode = blackNow;
                    await RotateAsync();
                }
                else if (!blackNow && schedule.IsDue(DateTime.UtcNow)) await RotateAsync();
            };
            timer.Start();
            log.Write("Started. Target " + settings.Width + "x" + settings.Height + ", interval " + settings.IntervalMinutes + " minutes.");
        }

        private async Task RotateAsync()
        {
            if (busy || closing) return;
            busy = true;
            next.Enabled = false;
            status.Text = "Downloading...";
            try
            {
                ApplySettings(Settings.Load(root));
                bool blackNow = UsesBlackBackgroundNow(DateTime.Now);
                backgroundModeInitialized = true;
                lastBlackMode = blackNow;
                if (blackNow)
                {
                    WindowsIntegration.SetBlackBackground();
                    icon.Icon = trayIcon;
                    status.Text = "Black";
                    log.Write("Applied black background.");
                    return;
                }
                rotation = new Rotation(
                    new Wallhaven(http, settings, Path.Combine(root, "wallpapers")),
                    settings, root, ApplyWallpaperFromCurrentConfig, delegate { WindowsIntegration.SetBlackBackground(); }, log);
                RotationResult result = await rotation.NextAsync(cancellation.Token);
                if (closing) return;
                blackNow = UsesBlackBackgroundNow(DateTime.Now);
                lastBlackMode = blackNow;
                if (blackNow)
                {
                    WindowsIntegration.SetBlackBackground();
                    icon.Icon = trayIcon;
                    status.Text = "Black";
                    return;
                }
                icon.Icon = result.UsedCache || !result.Changed ? SystemIcons.Warning : trayIcon;
                status.Text = result.Message.Contains("applied a black background")
                    ? "Black (no saved image)"
                    : (result.Changed ? (result.UsedCache ? "Saved" : "New") : "Failed") + " at " + DateTime.Now.ToShortTimeString();
                if (result.UsedCache || !result.Changed) Notify("Wallpaper download failed", result.Message);
            }
            catch (OperationCanceledException) { if (!closing) Notify("Wallpaper rotation failed", "The operation was canceled."); }
            catch (Exception error)
            {
                if (!closing)
                {
                    string message = rotation == null ? error.Message : rotation.SafeMessage(error);
                    log.Write("Rotation failed: " + message);
                    status.Text = "Rotation failed";
                    icon.Icon = SystemIcons.Warning;
                    Notify("Wallpaper rotation failed", message);
                }
            }
            finally
            {
                schedule.Completed(DateTime.UtcNow);
                busy = false;
                if (!closing) next.Enabled = !settings.BlackBackground;
            }
        }

        private async Task RequestNextWallpaperAsync()
        {
            ReloadConfigIfChanged();
            DateTime now = DateTime.Now;
            scheduledBlackSuppressedUntil = settings.BlackBackground
                ? null
                : BackgroundPolicy.ScheduledBlackWindowEnd(settings, now);
            await RotateAsync();
        }

        private bool UsesBlackBackgroundNow(DateTime localTime)
        {
            if (scheduledBlackSuppressedUntil.HasValue && localTime >= scheduledBlackSuppressedUntil.Value)
                scheduledBlackSuppressedUntil = null;
            return BackgroundPolicy.UsesBlackBackground(settings, localTime, scheduledBlackSuppressedUntil.HasValue);
        }

        internal bool ReloadConfigIfChanged()
        {
            string path = Path.Combine(root, "config.jsonc");
            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { return false; }
            if (writeTime == configLastWriteUtc) return false;
            try
            {
                bool backgroundChanged = ApplySettings(Settings.Load(root));
                configLastWriteUtc = File.GetLastWriteTimeUtc(path);
                rejectedConfigWriteUtc = DateTime.MinValue;
                log.Write("Reloaded config.jsonc.");
                return backgroundChanged;
            }
            catch (Exception error)
            {
                if (rejectedConfigWriteUtc != writeTime)
                {
                    rejectedConfigWriteUtc = writeTime;
                    log.Write("Could not reload config.jsonc: " + error.Message);
                    Notify("WallpaperCycle config is invalid", error.Message);
                }
                return false;
            }
        }

        private bool ApplySettings(Settings updated)
        {
            DateTime localNow = DateTime.Now;
            bool wasBlack = UsesBlackBackgroundNow(localNow);
            if (settings.BlackBackgroundScheduleEnabled != updated.BlackBackgroundScheduleEnabled ||
                settings.BlackBackgroundStartHour != updated.BlackBackgroundStartHour ||
                settings.BlackBackgroundEndHour != updated.BlackBackgroundEndHour)
                scheduledBlackSuppressedUntil = null;
            settings = updated;
            bool backgroundChanged = wasBlack != UsesBlackBackgroundNow(localNow);
            configLastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(root, "config.jsonc"));
            schedule.SetIntervalMinutes(settings.IntervalMinutes);
            BlackBackgroundMenuItem.Checked = settings.BlackBackground;
            next.Enabled = !settings.BlackBackground && !busy;
            return backgroundChanged;
        }

        private void ApplyWallpaperFromCurrentConfig(string path)
        {
            ApplySettings(Settings.Load(root));
            configLastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(root, "config.jsonc"));
            bool blackNow = UsesBlackBackgroundNow(DateTime.Now);
            backgroundModeInitialized = true;
            lastBlackMode = blackNow;
            if (blackNow) WindowsIntegration.SetBlackBackground();
            else
            {
                Wallhaven.ValidateImage(path, settings);
                WindowsIntegration.SetWallpaper(path);
            }
        }

        internal string FavoriteCurrentWallpaper()
        {
            if (UsesBlackBackgroundNow(DateTime.Now))
            {
                Notify("No wallpaper to favorite", "The desktop is currently using a black background.");
                return null;
            }
            try
            {
                string currentPath = File.ReadAllText(Path.Combine(root, "data", "current.txt")).Trim();
                string savedPath = FavoriteStore.Save(root, currentPath);
                log.Write("Favorited: " + Path.GetFileName(savedPath));
                Notify("Favorited", Path.GetFileName(savedPath));
                return savedPath;
            }
            catch (Exception error)
            {
                log.Write("Could not favorite wallpaper: " + error.Message);
                Notify("Could not favorite wallpaper", error.Message);
                return null;
            }
        }

        private void Notify(string title, string message)
        {
            icon.ShowBalloonTip(10000, title, message.Length > 255 ? message.Substring(0, 252) + "..." : message, ToolTipIcon.Warning);
        }

        private void RequestExit()
        {
            if (closing) return;
            closing = true;
            cancellation.Cancel();
            timer.Stop();
            icon.Visible = false;
            ExitThread();
        }

        private void Open(string executable, string path)
        {
            try { Process.Start(new ProcessStartInfo(executable, "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception error) { Notify("Could not open file", error.Message); }
        }

        protected override void ExitThreadCore()
        {
            RequestExitState();
            base.ExitThreadCore();
        }

        private void RequestExitState()
        {
            if (closing) return;
            closing = true;
            cancellation.Cancel();
            timer.Stop();
            icon.Visible = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                RequestExitState();
                timer.Dispose();
                icon.ContextMenuStrip.Dispose();
                icon.Dispose();
                trayIcon.Dispose();
                http.Dispose();
                cancellation.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
