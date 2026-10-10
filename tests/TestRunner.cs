using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WallpaperCycle;

internal static class TestRunner
{
    private static int passed;
    private static int failed;
    private static string testDirectory;
    private static readonly byte[] Png = ImageBytes(120, 50);

    [STAThread]
    private static void Main(string[] args)
    {
        testDirectory = Path.Combine(Path.GetFullPath(args[0]), "data", "test-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        Run("partial configuration retains defaults", delegate {
            string root = Root();
            File.WriteAllText(Path.Combine(root, "config.jsonc"), "// defaults survive comments\n{\"IntervalMinutes\": 90}");
            Settings settings = Settings.Load(root);
            Assert(settings.IntervalMinutes == 90 && settings.Width == 3440 && settings.Purity == "100" && !settings.BlackBackground && settings.Sorting == "random", "Defaults were lost.");
            string saved = File.ReadAllText(Path.Combine(root, "config.jsonc"));
            Assert(saved.Contains("// Choosing Next wallpaper skips black until the configured window ends."), "Schedule override guidance was not included in config comments.");
            Assert(saved.Contains("// Query examples:") && saved.Contains("+forest +sunset") && saved.Contains("id:123"), "Wallhaven query examples were not included in config comments.");
        });
        Run("legacy JSON config is imported into commented JSONC config", delegate {
            string root = Root();
            string legacy = Path.Combine(root, "config.json");
            File.WriteAllText(legacy, "{\"IntervalMinutes\": 35, \"Query\": \"landscape\"}");
            Settings settings = Settings.Load(root);
            string current = Path.Combine(root, "config.jsonc");
            Assert(settings.IntervalMinutes == 35 && settings.Query == "landscape", "Legacy config values were not imported.");
            Assert(File.Exists(current) && File.ReadAllText(current).Contains("// Wallhaven sorting"), "Commented config was not created during migration.");
            Assert(File.Exists(legacy), "Legacy config was unexpectedly removed.");
        });
        Run("commented config preserves comment markers inside strings and saves editable defaults", delegate {
            string root = Root();
            Settings settings = new Settings { Query = "space // stars", Sorting = "toplist", Colors = "blue,white" };
            settings.Save(root);
            string path = Path.Combine(root, "config.jsonc");
            string file = File.ReadAllText(path);
            Settings loaded = Settings.Load(root);
            Assert(file.Contains("// Wallhaven sorting") && file.Contains("// Dominant color names") && file.Contains("// Rotation") && file.Contains("// Image matching"), "Saved config sections or comments are missing.");
            Assert(loaded.Query == settings.Query && loaded.Sorting == "toplist" && loaded.Colors == "blue,white", "Comment parsing changed setting values.");
        });
        Run("invalid configuration is rejected", delegate {
            Expect<InvalidDataException>(delegate { new Settings { IntervalMinutes = 0 }.Validate(); });
            Expect<InvalidDataException>(delegate { new Settings { AspectRatioTolerance = 0.26 }.Validate(); });
            new Settings { Purity = "111", ApiKey = "" }.Validate();
            Expect<InvalidDataException>(delegate { new Settings { Categories = "000" }.Validate(); });
            Expect<InvalidDataException>(delegate { new Settings { Sorting = "newest" }.Validate(); });
            Expect<InvalidDataException>(delegate { new Settings { Colors = "#000000" }.Validate(); });
            Expect<InvalidDataException>(delegate { new Settings { Colors = "ultraviolet" }.Validate(); });
            Expect<InvalidDataException>(delegate { new Settings { BlackBackgroundStartHour = 24 }.Validate(); });
            Expect<InvalidDataException>(delegate { new Settings { BlackBackgroundStartHour = 7, BlackBackgroundEndHour = 7 }.Validate(); });
        });
        Run("ultrawide dimensions allow modest cropping and reject poor matches", delegate {
            Settings settings = new Settings();
            Assert(settings.Accepts(3440, 1440) && settings.Accepts(5120, 2160), "Suitable ultrawide rejected.");
            Assert(!settings.Accepts(3840, 2160) && !settings.Accepts(2560, 1080), "Unsuitable image accepted.");
        });
        Run("search filters and query encoding", delegate {
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.OK, Png)))
            {
                string url = new Wallhaven(http, new Settings { Query = "space & stars", ApiKey = "secret", Sorting = "date_added", Colors = "blue,white" }, Walls(Root())).SearchUrl();
                Assert(url.Contains("sorting=date_added") && url.Contains("atleast=3440x1440") && url.Contains("ratios=21x9") && url.Contains("q=space%20%26%20stars") && url.Contains("colors=0066cc%2Cffffff"), "Named colors were not converted to Wallhaven color codes.");
                Assert(!url.Contains("secret"), "API key exposed in URL.");
            }
        });
        Run("search advances through result pages and resumes its cursor after restart", delegate {
            string root = Root();
            Settings settings = SmallSettings();
            settings.Sorting = "views";
            settings.Query = "warcraft";
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png);
            handler.SearchResponses[1] = SearchPage("abcdef", 1, 2);
            handler.SearchResponses[2] = SearchPage("ghijkl", 2, 2);
            using (HttpClient http = new HttpClient(handler))
            {
                Wallhaven firstPage = new Wallhaven(http, settings, Walls(root));
                string first = firstPage.DownloadNewAsync(CancellationToken.None).GetAwaiter().GetResult();
                Wallhaven nextPage = new Wallhaven(http, settings, Walls(root));
                Assert(nextPage.SearchUrl().Contains("page=2"), "Next page was not saved for the next rotation.");
                string second = nextPage.DownloadNewAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(Path.GetFileName(first) == "wallhaven-abcdef.png" && Path.GetFileName(second) == "wallhaven-ghijkl.png" && handler.SearchCalls == 2, "Rotation did not use one API call per page and wallpaper.");
                Wallhaven wrapped = new Wallhaven(http, settings, Walls(root));
                Assert(wrapped.SearchUrl().Contains("page=1"), "Page cursor did not wrap after the last result page.");
            }
        });
        Run("successful download validates and preserves existing images", delegate {
            string root = Root();
            string previous = Save(root, "previous.png", Png);
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png);
            using (HttpClient http = new HttpClient(handler))
            {
                string applied = null;
                RotationResult result = Engine(root, http, delegate(string path) { applied = path; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Changed && !result.UsedCache, "Online download did not apply.");
                Assert(Path.GetFileName(applied) == "wallhaven-abcdef.png" && File.Exists(applied), "Downloaded file missing.");
                Assert(File.Exists(previous) && Directory.GetFiles(Walls(root), "*.part").Length == 0, "Archive changed or partial remains.");
                Assert(handler.SearchHadKey && !handler.DownloadHadKey, "API key sent to the wrong host.");
            }
        });
        Run("HTTP failure rotates saved images without immediate repeats, including after restart", delegate {
            string root = Root();
            Save(root, "one.png", Png);
            Save(root, "two.png", Png);
            Save(root, "three.png", Png);
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.ServiceUnavailable, Png)))
            {
                string applied = null;
                Action<string> setter = delegate(string path) { applied = path; };
                Rotation engine = Engine(root, http, setter);
                RotationResult result = engine.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                string first = applied;
                Assert(result.Changed && result.UsedCache && result.Message.Contains("503"), "Failure reason or cache result missing.");
                engine.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(applied != first, "Immediate repeat during offline rotation.");
                string second = applied;
                Engine(root, http, setter).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(applied != second && Directory.GetFiles(Walls(root)).Length == 3, "Restart repeated the current image or pruned the archive.");
            }
        });
        Run("corrupt cached images are skipped and retained", delegate {
            string root = Root();
            string valid = Save(root, "valid.png", Png);
            string corrupt = Save(root, "corrupt.png", Encoding.UTF8.GetBytes("not an image"));
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.ServiceUnavailable, Png)))
            {
                string applied = null;
                RotationResult result = Engine(root, http, delegate(string path) { applied = path; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.UsedCache && applied == valid && File.Exists(corrupt), "Corrupt cache handling incorrect.");
            }
        });
        Run("failure without cached images applies black", delegate {
            string root = Root();
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.Forbidden, Png)))
            {
                int blackCalls = 0;
                RotationResult result = Engine(root, http, delegate { }, delegate { blackCalls++; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Changed && result.Message.Contains("403") && result.Message.Contains("black background") && blackCalls == 1, "No-image fallback did not apply black or preserve the failure reason.");
            }
        });
        Run("missing NSFW key warns during rotation and falls back to a favorite", delegate {
            string root = Root();
            string favorite = SaveFavorite(root, "sfw-fallback.png", Png);
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png);
            Settings settings = SmallSettings();
            settings.Purity = "001";
            settings.ApiKey = "";
            int blackCalls = 0;
            string applied = null;
            using (HttpClient http = new HttpClient(handler))
            {
                Rotation engine = new Rotation(new Wallhaven(http, settings, Walls(root)), settings, root, delegate(string path) { applied = path; }, delegate { blackCalls++; }, new AppLog(root));
                RotationResult result = engine.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.UsedCache && result.Message.Contains("valid Wallhaven API key") && applied == favorite && blackCalls == 0 && handler.Calls == 0, "Missing NSFW key did not warn and fall back to a favorite.");
            }
        });
        Run("offline rotation prefers favorites over downloaded wallpapers", delegate {
            string root = Root();
            string downloaded = Save(root, "downloaded.png", Png);
            string favorite = SaveFavorite(root, "favorite.png", Png);
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.ServiceUnavailable, Png)))
            {
                string applied = null;
                RotationResult result = Engine(root, http, delegate(string path) { applied = path; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.UsedCache && applied == favorite && File.Exists(downloaded), "Offline rotation did not prioritize favorites or changed the archive.");
            }
        });
        Run("already archived search results use cache without downloading again", delegate {
            string root = Root();
            Save(root, "wallhaven-abcdef.png", Png);
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png);
            using (HttpClient http = new HttpClient(handler))
            {
                RotationResult result = Engine(root, http, delegate { }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Changed && result.UsedCache && handler.Calls == 1, "Already saved image was downloaded again.");
            }
        });
        Run("actual downloaded dimensions are checked independently of metadata", delegate {
            string root = Root();
            string previous = Save(root, "previous.png", Png);
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.OK, ImageBytes(120, 120))))
            {
                string applied = null;
                RotationResult result = Engine(root, http, delegate(string path) { applied = path; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.UsedCache && applied == previous, "Mismatched image was applied.");
                Assert(!File.Exists(Path.Combine(Walls(root), "wallhaven-abcdef.png")) && Directory.GetFiles(Walls(root), "*.part").Length == 0, "Invalid download was saved.");
            }
        });
        Run("interrupted image downloads remove only their unfinished file", delegate {
            string root = Root();
            string previous = Save(root, "previous.png", Png);
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png) { InterruptDownload = true };
            using (HttpClient http = new HttpClient(handler))
            {
                RotationResult result = Engine(root, http, delegate { }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.UsedCache && File.Exists(previous) && Directory.GetFiles(Walls(root)).Length == 1, "Interrupted download damaged archive or left debris.");
            }
        });
        Run("cancellation stops without applying an offline image", delegate {
            string root = Root();
            Save(root, "previous.png", Png);
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.OK, Png)))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                int calls = 0;
                Expect<OperationCanceledException>(delegate { Engine(root, http, delegate { calls++; }).NextAsync(cancellation.Token).GetAwaiter().GetResult(); });
                Assert(calls == 0, "Wallpaper changed after cancellation.");
            }
        });
        Run("Windows application failure leaves the downloaded archive intact", delegate {
            string root = Root();
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.OK, Png)))
            {
                Expect<InvalidOperationException>(delegate {
                    Engine(root, http, delegate { throw new InvalidOperationException("Windows failure"); }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                });
                Assert(File.Exists(Path.Combine(Walls(root), "wallhaven-abcdef.png")), "Windows failure lost a successful download.");
            }
        });
        Run("only cached image remains usable after restart", delegate {
            string root = Root();
            string only = Save(root, "only.png", Png);
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "data", "current.txt"), only);
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.ServiceUnavailable, Png)))
            {
                string applied = null;
                Engine(root, http, delegate(string path) { applied = path; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(applied == only, "Only cached image could not be used.");
            }
        });
        Run("startup, hourly timing, pause, and resume after sleep", delegate {
            DateTime now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
            RotationSchedule schedule = new RotationSchedule(60, now);
            Assert(schedule.IsDue(now), "No startup rotation.");
            schedule.Completed(now);
            Assert(!schedule.IsDue(now.AddMinutes(59)) && schedule.IsDue(now.AddHours(1)), "Hourly timing incorrect.");
            schedule.Paused = true;
            Assert(!schedule.IsDue(now.AddHours(3)), "Pause ignored.");
            schedule.Paused = false;
            Assert(schedule.IsDue(now.AddHours(3)), "Missed interval did not run after wake.");
            schedule.Completed(now.AddHours(3));
            Assert(!schedule.IsDue(now.AddHours(3).AddSeconds(1)), "Missed intervals created a burst of rotations.");
        });
        Run("black schedule covers midnight until the exclusive end and supports overnight windows", delegate {
            Settings settings = new Settings { BlackBackgroundScheduleEnabled = true, BlackBackgroundStartHour = 0, BlackBackgroundEndHour = 7 };
            Assert(BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 0, 0, 0)), "Midnight was not included.");
            Assert(BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 6, 59, 0)), "The hour before 7 AM was not included.");
            Assert(!BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 7, 0, 0)), "The exclusive end time was included.");
            DateTime scheduledEnd = BackgroundPolicy.ScheduledBlackWindowEnd(settings, new DateTime(2026, 10, 9, 1, 0, 0)).Value;
            Assert(scheduledEnd == new DateTime(2026, 10, 9, 7, 0, 0), "Manual override does not expire at the scheduled end.");
            Assert(!BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 1, 0, 0), true), "Manual override could not suppress scheduled black.");
            settings.BlackBackgroundStartHour = 22;
            settings.BlackBackgroundEndHour = 7;
            Assert(BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 23, 0, 0)) && BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 6, 0, 0)), "Overnight window was not covered.");
            Assert(BackgroundPolicy.ScheduledBlackWindowEnd(settings, new DateTime(2026, 10, 9, 23, 0, 0)).Value == new DateTime(2026, 10, 10, 7, 0, 0), "Overnight override did not expire the next morning.");
            Assert(!BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 12, 0, 0)), "Daytime was included in overnight window.");
            settings.BlackBackgroundScheduleEnabled = false;
            Assert(!BackgroundPolicy.UsesBlackBackground(settings, new DateTime(2026, 10, 9, 1, 0, 0)), "Disabled schedule still used black.");
        });
        Run("interval changes reload without restarting", delegate {
            string root = Root();
            string config = Path.Combine(root, "config.jsonc");
            Settings initial = new Settings { Query = "first", BlackBackgroundScheduleEnabled = false };
            initial.Save(root);
            DateTime oldWriteTime = File.GetLastWriteTimeUtc(config);
            using (TrayApplication app = new TrayApplication(root, Settings.Load(root)))
            {
                Settings updated = new Settings { Query = "second", IntervalMinutes = 15, BlackBackgroundScheduleEnabled = false };
                updated.Save(root);
                File.SetLastWriteTimeUtc(config, oldWriteTime.AddSeconds(5));
                Assert(!app.ReloadConfigIfChanged(), "Changing unrelated settings was reported as a background mode change.");
                Assert(app.CurrentSettings.Query == "second" && app.CurrentSettings.IntervalMinutes == 15, "Config edits were not loaded into the running app.");

                updated.BlackBackground = true;
                updated.Save(root);
                File.SetLastWriteTimeUtc(config, oldWriteTime.AddSeconds(10));
                Assert(app.ReloadConfigIfChanged(), "Changing BlackBackground was not detected.");
                Assert(app.CurrentSettings.BlackBackground && app.BlackBackgroundMenuItem.Checked, "The running menu did not reflect the updated config.");
            }
        });
        Run("tray black background option saves config and applies black", delegate {
            string previous = WindowsIntegration.GetWallpaper();
            if (string.IsNullOrWhiteSpace(previous) || !File.Exists(previous))
                throw new InvalidOperationException("This tray integration test needs an existing wallpaper to restore afterward.");
            string root = Root();
            new Settings().Save(root);
            bool restore = false;
            try
            {
                using (TrayApplication app = new TrayApplication(root, Settings.Load(root)))
                {
                    restore = true;
                    app.BlackBackgroundMenuItem.PerformClick();
                    Settings saved = Settings.Load(root);
                    string blackPath = WindowsIntegration.GetWallpaper();
                    Assert(saved.BlackBackground && app.CurrentSettings.BlackBackground, "The menu did not save the black background setting.");
                    Assert(Path.GetFileName(blackPath) == "black-background.bmp", "The menu did not apply the black background.");
                }
            }
            finally { if (restore) WindowsIntegration.SetWallpaper(previous); }
        });
        Run("tray favorite action copies the current wallpaper into the repository favorites folder", delegate {
            string root = Root();
            new Settings { BlackBackgroundScheduleEnabled = false }.Save(root);
            string current = Save(root, "wallhaven-favorite.png", Png);
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "data", "current.txt"), current);
            using (TrayApplication app = new TrayApplication(root, Settings.Load(root)))
            {
                Assert(app.FavoriteMenuItem.Text == "Favorite" && app.BlackBackgroundMenuItem.Text == "Black", "Tray menu labels were not concise.");
                app.FavoriteMenuItem.PerformClick();
                app.FavoriteMenuItem.PerformClick();
                string favorite = Path.Combine(root, "favorites", "wallhaven-favorite.png");
                Assert(File.Exists(favorite) && File.ReadAllBytes(favorite).Length == File.ReadAllBytes(current).Length && Directory.GetFiles(Path.Combine(root, "favorites")).Length == 1, "Tray favorite action did not save one copy of the current image.");
            }
        });
        Run("custom tray icon contains the wallpaper landscape artwork", delegate {
            using (Icon icon = TrayIcon.Create())
            using (Bitmap bitmap = icon.ToBitmap())
            {
                Assert(icon.Width == 32 && icon.Height == 32, "Tray icon has unexpected dimensions.");
                bool foundSun = false;
                for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R == 255 && pixel.G == 194 && pixel.B == 91) foundSun = true;
                }
                Assert(foundSun, "The custom sun artwork is missing from the tray icon.");
            }
        });
        Run("tray Exit closes the message loop and hides its icon", delegate {
            string root = Root();
            using (TrayApplication app = new TrayApplication(root, SmallSettings()))
            using (System.Windows.Forms.Timer clickExit = new System.Windows.Forms.Timer())
            using (System.Windows.Forms.Timer timeout = new System.Windows.Forms.Timer())
            {
                bool timedOut = false;
                clickExit.Interval = 50;
                clickExit.Tick += delegate { clickExit.Stop(); app.ExitMenuItem.PerformClick(); };
                timeout.Interval = 3000;
                timeout.Tick += delegate { timedOut = true; timeout.Stop(); app.ExitThread(); };
                clickExit.Start();
                timeout.Start();
                Application.Run(app);
                Assert(!timedOut, "The Exit menu did not close the message loop before the timeout.");
                Assert(app.IsClosing && !app.IsTrayIconVisible, "Exit left the tray icon visible or the app open.");
            }
        });
        Run("API keys are removed from failure messages", delegate {
            string root = Root();
            using (HttpClient http = new HttpClient(new FakeHandler(HttpStatusCode.OK, Png)))
            {
                Rotation engine = Engine(root, http, delegate { });
                string message = engine.SafeMessage(new HttpRequestException("Request failed", new IOException("test-key was rejected\nagain")));
                Assert(message.Contains("IOException") && message.Contains("[redacted]") && !message.Contains("test-key") && !message.Contains("\n"), "Inner API key leaked into failure text.");
            }
        });
        Run("nested connection errors reach the notification and log", delegate {
            string root = Root();
            Save(root, "saved.png", Png);
            Exception cause = new HttpRequestException("An error occurred while sending the request.",
                new IOException("TLS negotiation failed.", new System.ComponentModel.Win32Exception("No credentials are available in the security package.")));
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png) { SearchError = cause };
            using (HttpClient http = new HttpClient(handler))
            {
                RotationResult result = Engine(root, http, delegate { }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                string log = File.ReadAllText(Path.Combine(root, "data", "app.log"));
                Assert(result.UsedCache && result.Message.Contains("Win32Exception") && result.Message.Contains("No credentials"), "The actual connection failure was hidden from the notification.");
                Assert(log.Contains("IOException") && log.Contains("No credentials"), "The actual connection failure was hidden from the log.");
            }
        });
        Run("timeout errors are clear when there is no cached wallpaper", delegate {
            string root = Root();
            FakeHandler handler = new FakeHandler(HttpStatusCode.OK, Png) { SearchError = new OperationCanceledException("The request timed out.") };
            using (HttpClient http = new HttpClient(handler))
            {
                int blackCalls = 0;
                RotationResult result = Engine(root, http, delegate { }, delegate { blackCalls++; }).NextAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Changed && result.Message.Contains("Download timed out.") && result.Message.Contains("black background") && blackCalls == 1, "Timeout did not clearly fall back to black.");
            }
        });
        Run("application logs are written under the data directory", delegate {
            string root = Root();
            AppLog log = new AppLog(root);
            log.Write("diagnostic marker");
            string path = Path.Combine(root, "data", "app.log");
            Assert(File.Exists(path) && File.ReadAllText(path).Contains("diagnostic marker"), "Expected app.log was not written under data.");
        });
        Run("Windows actually changes wallpaper and restores the previous one", delegate {
            string previous = WindowsIntegration.GetWallpaper();
            if (string.IsNullOrWhiteSpace(previous) || !File.Exists(previous))
                throw new InvalidOperationException("This desktop integration test needs an existing wallpaper file so it can restore the desktop afterward.");
            string root = Root();
            string[] testImages = {
                Save(root, "temporary-wallpaper.bmp", ImageBytes(3440, 1440, ImageFormat.Bmp)),
                Save(root, "temporary-wallpaper.png", ImageBytes(3440, 1440))
            };
            foreach (string testImage in testImages)
            {
                bool changed = false;
                try
                {
                    WindowsIntegration.SetWallpaper(testImage);
                    changed = true;
                    string active = WindowsIntegration.GetWallpaper();
                    if (Path.GetExtension(testImage).Equals(".png", StringComparison.OrdinalIgnoreCase))
                        Assert(active.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase), "PNG wallpaper was not converted to a Windows-compatible JPEG.");
                    else
                        Assert(string.Equals(Path.GetFullPath(active), Path.GetFullPath(testImage), StringComparison.OrdinalIgnoreCase), "Windows did not report the test BMP as the active wallpaper.");
                }
                finally
                {
                    if (changed) WindowsIntegration.SetWallpaper(previous);
                }
            }
            bool restoreAfterBlackTest = false;
            try
            {
                restoreAfterBlackTest = true;
                string blackPath = WindowsIntegration.SetBlackBackground();
                Assert(string.Equals(Path.GetFullPath(WindowsIntegration.GetWallpaper()), Path.GetFullPath(blackPath), StringComparison.OrdinalIgnoreCase), "Windows did not apply the black background.");
                using (Bitmap black = new Bitmap(blackPath))
                    Assert(black.GetPixel(0, 0).R == 0 && black.GetPixel(0, 0).G == 0 && black.GetPixel(0, 0).B == 0, "The black background image is not black.");
            }
            finally { if (restoreAfterBlackTest) WindowsIntegration.SetWallpaper(previous); }
            Assert(string.Equals(Path.GetFullPath(WindowsIntegration.GetWallpaper()), Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase), "The original desktop wallpaper was not restored.");
        });
        Console.WriteLine("{0} passed, {1} failed. Fixtures: {2}", passed, failed, testDirectory);
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    private static Settings SmallSettings()
    {
        return new Settings { Width = 120, Height = 50, ApiKey = "test-key", BlackBackgroundScheduleEnabled = false };
    }

    private static Rotation Engine(string root, HttpClient http, Action<string> setter)
    {
        return Engine(root, http, setter, delegate { });
    }

    private static Rotation Engine(string root, HttpClient http, Action<string> setter, Action blackSetter)
    {
        Settings settings = SmallSettings();
        AppLog log = new AppLog(root);
        return new Rotation(new Wallhaven(http, settings, Walls(root)), settings, root, setter, blackSetter, log);
    }

    private static string Root()
    {
        string root = Path.Combine(testDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Walls(string root)
    {
        string directory = Path.Combine(root, "wallpapers");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Save(string root, string name, byte[] bytes)
    {
        string path = Path.Combine(Walls(root), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string SaveFavorite(string root, string name, byte[] bytes)
    {
        string directory = Path.Combine(root, "favorites");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ImageBytes(int width, int height)
    {
        return ImageBytes(width, height, ImageFormat.Png);
    }

    private static byte[] ImageBytes(int width, int height, ImageFormat format)
    {
        using (Bitmap image = new Bitmap(width, height))
        using (MemoryStream stream = new MemoryStream())
        {
            image.Save(stream, format);
            return stream.ToArray();
        }
    }

    private static string SearchPage(string id, int currentPage, int lastPage)
    {
        return "{\"data\":[{\"id\":\"" + id + "\",\"dimension_x\":120,\"dimension_y\":50,\"path\":\"https://w.wallhaven.cc/full/ab/wallhaven-" + id + ".png\"}],\"meta\":{\"current_page\":" + currentPage + ",\"last_page\":" + lastPage + "}}";
    }

    private static void Run(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode searchStatus;
        private readonly byte[] image;
        public bool SearchHadKey;
        public bool DownloadHadKey;
        public int Calls;
        public bool InterruptDownload;
        public Exception SearchError;
        public int SearchCalls;
        public readonly Dictionary<int, string> SearchResponses = new Dictionary<int, string>();

        public FakeHandler(HttpStatusCode searchStatus, byte[] image)
        {
            this.searchStatus = searchStatus;
            this.image = image;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Calls++;
            HttpResponseMessage response;
            if (request.RequestUri.Host == "wallhaven.cc")
            {
                if (SearchError != null) return Task.FromException<HttpResponseMessage>(SearchError);
                SearchCalls++;
                SearchHadKey = request.Headers.Contains("X-API-Key");
                int page = 1;
                foreach (string parameter in request.RequestUri.Query.TrimStart('?').Split('&'))
                {
                    string[] pair = parameter.Split('=');
                    if (pair.Length == 2 && pair[0] == "page") int.TryParse(pair[1], out page);
                }
                string searchJson;
                if (!SearchResponses.TryGetValue(page, out searchJson))
                    searchJson = "{\"data\":[{\"id\":\"abcdef\",\"dimension_x\":120,\"dimension_y\":50,\"path\":\"https://w.wallhaven.cc/full/ab/wallhaven-abcdef.png\"}],\"meta\":{\"current_page\":1,\"last_page\":1}}";
                response = new HttpResponseMessage(searchStatus) {
                    Content = new StringContent(searchJson)
                };
            }
            else
            {
                DownloadHadKey = request.Headers.Contains("X-API-Key");
                response = new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = InterruptDownload ? (HttpContent)new StreamContent(new InterruptedStream(image)) : new ByteArrayContent(image)
                };
            }
            return Task.FromResult(response);
        }
    }

    private sealed class InterruptedStream : MemoryStream
    {
        private bool first = true;
        public InterruptedStream(byte[] image) : base(image) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
        {
            if (first) { first = false; return base.ReadAsync(buffer, offset, Math.Min(32, count), cancellation); }
            throw new IOException("Connection interrupted.");
        }
    }
}
