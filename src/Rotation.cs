using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WallpaperCycle
{
    public sealed class RotationResult
    {
        public bool Changed { get; set; }
        public bool UsedCache { get; set; }
        public string Message { get; set; }
    }

    public sealed class Rotation
    {
        private readonly Wallhaven wallhaven;
        private readonly Settings settings;
        private readonly string directory;
        private readonly string favoritesDirectory;
        private readonly string currentFile;
        private readonly Action<string> setWallpaper;
        private readonly Action setBlackBackground;
        private readonly AppLog log;
        private readonly Random random = new Random();
        private string current;

        public Rotation(Wallhaven wallhaven, Settings settings, string root, Action<string> setWallpaper, AppLog log)
            : this(wallhaven, settings, root, setWallpaper, delegate { WindowsIntegration.SetBlackBackground(); }, log)
        {
        }

        public Rotation(Wallhaven wallhaven, Settings settings, string root, Action<string> setWallpaper, Action setBlackBackground, AppLog log)
        {
            this.wallhaven = wallhaven;
            this.settings = settings;
            this.directory = Path.Combine(root, "wallpapers");
            this.favoritesDirectory = Path.Combine(root, "favorites");
            this.currentFile = Path.Combine(root, "data", "current.txt");
            this.setWallpaper = setWallpaper;
            this.setBlackBackground = setBlackBackground;
            this.log = log;
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(favoritesDirectory);
            current = File.Exists(currentFile) ? File.ReadAllText(currentFile).Trim() : "";
        }

        public async Task<RotationResult> NextAsync(CancellationToken cancellation)
        {
            string next;
            string failure = null;
            try { next = await wallhaven.DownloadNewAsync(cancellation); }
            catch (Exception error)
            {
                cancellation.ThrowIfCancellationRequested();
                failure = SafeMessage(error);
                log.Write("Download failed: " + failure);
                next = FindCached();
            }
            cancellation.ThrowIfCancellationRequested();
            if (next == null)
            {
                setBlackBackground();
                current = "";
                try { File.WriteAllText(currentFile, ""); }
                catch (IOException error) { log.Write("Could not save current wallpaper state: " + error.Message); }
                catch (UnauthorizedAccessException error) { log.Write("Could not save current wallpaper state: " + error.Message); }
                log.Write("No usable saved wallpaper was available; applied black background.");
                return new RotationResult { Changed = true, UsedCache = true, Message = failure + " No usable saved wallpaper was available; applied a black background." };
            }

            // A Windows wallpaper error is reported separately from a download error.
            setWallpaper(next);
            current = next;
            try { File.WriteAllText(currentFile, next); }
            catch (IOException error) { log.Write("Could not save current wallpaper state: " + error.Message); }
            catch (UnauthorizedAccessException error) { log.Write("Could not save current wallpaper state: " + error.Message); }
            log.Write((failure == null ? "Downloaded: " : "Offline rotation: ") + Path.GetFileName(next));
            return new RotationResult {
                Changed = true,
                UsedCache = failure != null,
                Message = failure == null ? "New wallpaper: " + Path.GetFileName(next) : failure + " Using saved wallpaper " + Path.GetFileName(next) + "."
            };
        }

        private string FindCached()
        {
            // Favorites take precedence. If all favorites are unusable, continue through the downloaded archive.
            string[] files = new[] { favoritesDirectory, directory }
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path)
                    .Where(p => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    .OrderBy(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(p => random.Next()))
                .ToArray();
            foreach (string file in files)
            {
                try { Wallhaven.ValidateImage(file, settings); return file; }
                catch (Exception error) { log.Write("Skipping unusable saved image " + Path.GetFileName(file) + ": " + error.Message); }
            }
            return null;
        }

        public string SafeMessage(Exception error)
        {
            if (error is OperationCanceledException) return "Download timed out.";
            List<string> details = new List<string>();
            Exception current = error;
            int depth = 0;
            while (current != null && depth < 8)
            {
                details.Insert(0, current.GetType().Name + ": " + current.Message);
                current = current.InnerException;
                depth++;
            }
            string message = string.Join(" <- ", details);
            if (!string.IsNullOrEmpty(settings.ApiKey)) message = message.Replace(settings.ApiKey, "[redacted]");
            return message.Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    public sealed class RotationSchedule
    {
        public DateTime NextUtc { get; private set; }
        public bool Paused { get; set; }
        private TimeSpan interval;
        private readonly DateTime startedUtc;
        private DateTime lastCompletedUtc;
        private bool hasCompleted;

        public RotationSchedule(int intervalMinutes, DateTime now)
        {
            interval = TimeSpan.FromMinutes(intervalMinutes);
            NextUtc = now;
            startedUtc = now;
        }

        public bool IsDue(DateTime now) { return !Paused && now >= NextUtc; }
        public void SetIntervalMinutes(int intervalMinutes)
        {
            interval = TimeSpan.FromMinutes(intervalMinutes);
            NextUtc = hasCompleted ? lastCompletedUtc.Add(interval) : startedUtc;
        }
        public void Completed(DateTime now)
        {
            hasCompleted = true;
            lastCompletedUtc = now;
            NextUtc = now.Add(interval);
        }
    }
}
