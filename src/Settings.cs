using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WallpaperCycle
{
    public sealed class Settings
    {
        private const string ConfigFileName = "config.jsonc";
        private const string LegacyConfigFileName = "config.json";
        public int IntervalMinutes { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double AspectRatioTolerance { get; set; }
        public string Query { get; set; }
        public string Categories { get; set; }
        public string Purity { get; set; }
        public string ApiKey { get; set; }
        public int RequestTimeoutSeconds { get; set; }
        public bool BlackBackground { get; set; }
        public string Sorting { get; set; }
        public string Colors { get; set; }
        public bool BlackBackgroundScheduleEnabled { get; set; }
        public int BlackBackgroundStartHour { get; set; }
        public int BlackBackgroundEndHour { get; set; }

        public Settings()
        {
            IntervalMinutes = 60;
            Width = 3440;
            Height = 1440;
            AspectRatioTolerance = 0.10;
            Query = "";
            Categories = "100";
            Purity = "100";
            ApiKey = "";
            RequestTimeoutSeconds = 30;
            BlackBackground = false;
            Sorting = "random";
            Colors = "";
            BlackBackgroundScheduleEnabled = true;
            BlackBackgroundStartHour = 0;
            BlackBackgroundEndHour = 7;
        }

        public static Settings Load(string root)
        {
            string path = Path.Combine(root, ConfigFileName);
            if (!File.Exists(path))
            {
                string legacyPath = Path.Combine(root, LegacyConfigFileName);
                if (File.Exists(legacyPath))
                {
                    Settings legacy = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(legacyPath));
                    if (legacy == null) throw new InvalidDataException("config.json must contain a JSON object.");
                    legacy.Validate();
                    legacy.Save(root);
                    return legacy;
                }
                File.Copy(Path.Combine(root, "config.example.jsonc"), path);
            }
            string contents = File.ReadAllText(path);
            Settings settings = new JavaScriptSerializer().Deserialize<Settings>(RemoveComments(contents));
            if (settings == null) throw new InvalidDataException("config.jsonc must contain a JSON object.");
            settings.Validate();
            if (!contents.Contains("\"BlackBackgroundScheduleEnabled\"") || !contents.Contains("// Query examples:") ||
                !contents.Contains("// Dominant color names,") ||
                !contents.Contains("// Choosing Next wallpaper skips black until the configured window ends."))
                settings.Save(root);
            return settings;
        }

        public void Validate()
        {
            if (IntervalMinutes < 1 || IntervalMinutes > 10080)
                throw new InvalidDataException("IntervalMinutes must be between 1 and 10080.");
            if (Width < 1 || Height < 1 || Width > 16384 || Height > 16384)
                throw new InvalidDataException("Width and Height must be between 1 and 16384.");
            if (double.IsNaN(AspectRatioTolerance) || AspectRatioTolerance < 0 || AspectRatioTolerance > 0.25)
                throw new InvalidDataException("AspectRatioTolerance must be between 0 and 0.25.");
            if (RequestTimeoutSeconds < 5 || RequestTimeoutSeconds > 300)
                throw new InvalidDataException("RequestTimeoutSeconds must be between 5 and 300.");
            if (!new[] { "date_added", "relevance", "random", "views", "favorites", "toplist" }.Contains(Sorting))
                throw new InvalidDataException("Sorting must be date_added, relevance, random, views, favorites, or toplist.");
            if (!WallhavenColors.IsValid(Colors))
                throw new InvalidDataException("Colors must be empty or a comma-separated list of supported color names.");
            if (BlackBackgroundStartHour < 0 || BlackBackgroundStartHour > 23 || BlackBackgroundEndHour < 0 || BlackBackgroundEndHour > 23 || BlackBackgroundStartHour == BlackBackgroundEndHour)
                throw new InvalidDataException("Black background hours must be different whole hours from 0 through 23.");
            if (!Regex.IsMatch(Categories ?? "", "^[01]{3}$") || Categories == "000")
                throw new InvalidDataException("Categories must contain three 0/1 flags with at least one enabled.");
            if (!Regex.IsMatch(Purity ?? "", "^[01]{3}$") || Purity == "000")
                throw new InvalidDataException("Purity must contain three 0/1 flags with at least one enabled.");
        }

        public void Save(string root)
        {
            string path = Path.Combine(root, ConfigFileName);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            StringBuilder text = new StringBuilder();
            text.AppendLine("{");
            text.AppendLine("  // Rotation");
            text.AppendLine("  // Minutes between automatic wallpaper changes (1-10080). Example: 30 for every half hour.");
            text.AppendLine("  \"IntervalMinutes\": " + IntervalMinutes + ",");
            text.AppendLine();

            text.AppendLine("  // Image matching");
            text.AppendLine("  // Minimum wallpaper dimensions in pixels. Example: 3440 x 1440 for a 4K ultrawide.");
            text.AppendLine("  \"Width\": " + Width + ",");
            text.AppendLine("  \"Height\": " + Height + ",");
            text.AppendLine("  // Allowed relative aspect-ratio difference from the display (0-0.25). Example: 0.10 allows 10%.");
            text.AppendLine("  \"AspectRatioTolerance\": " + AspectRatioTolerance.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
            text.AppendLine();

            text.AppendLine("  // Wallhaven search");
            text.AppendLine("  // Query examples: landscape, +forest +sunset (require both tags), space -text (exclude a tag),");
            text.AppendLine("  // @username, type:jpg, like:wallpaperID, or id:123 (id: cannot be combined with other terms).");
            text.AppendLine("  // Prefix a tag with + to require it, or - to exclude it. See https://wallhaven.cc/help/api#search");
            text.AppendLine("  // Empty means no text filter. A narrow query may return no matching images.");
            text.AppendLine("  \"Query\": " + serializer.Serialize(Query ?? "") + ",");
            text.AppendLine("  // Three 0/1 flags in General, Anime, People order. Examples: 100=General, 111=all; not 000.");
            text.AppendLine("  \"Categories\": " + serializer.Serialize(Categories) + ",");
            text.AppendLine("  // Three 0/1 flags in SFW, Sketchy, NSFW order. Examples: 100=SFW, 110=SFW+Sketchy.");
            text.AppendLine("  // NSFW requires an API key; leave the third flag 0 to search without one.");
            text.AppendLine("  \"Purity\": " + serializer.Serialize(Purity) + ",");
            text.AppendLine("  // Wallhaven API key from https://wallhaven.cc/settings/account. Required only for NSFW.");
            text.AppendLine("  \"ApiKey\": " + serializer.Serialize(ApiKey ?? "") + ",");
            text.AppendLine("  // Request timeout in seconds (5-300). Example: 60 for a slower connection.");
            text.AppendLine("  \"RequestTimeoutSeconds\": " + RequestTimeoutSeconds + ",");
            text.AppendLine();

            text.AppendLine("  // Sorting and color");
            text.AppendLine("  // Wallhaven sorting: date_added, relevance, random, views, favorites, or toplist.");
            text.AppendLine("  // Examples: random for variety, views for popular results. Pages are advanced between rotations.");
            text.AppendLine("  \"Sorting\": " + serializer.Serialize(Sorting) + ",");
            text.AppendLine("  // Dominant color names, comma-separated: red, pink, purple, blue, cyan, green, yellow, orange, brown, black, gray, white.");
            text.AppendLine("  // Example: \"blue,white\". The app converts names to Wallhaven color codes.");
            text.AppendLine("  \"Colors\": " + serializer.Serialize(Colors ?? "") + ",");
            text.AppendLine();

            text.AppendLine("  // Black background");
            text.AppendLine("  // Keep the desktop solid black instead of applying rotated images. Default: false.");
            text.AppendLine("  \"BlackBackground\": " + (BlackBackground ? "true" : "false") + ",");
            text.AppendLine("  // Automatically use black during the configured local-time window, including after wake or sign-in.");
            text.AppendLine("  \"BlackBackgroundScheduleEnabled\": " + (BlackBackgroundScheduleEnabled ? "true" : "false") + ",");
            text.AppendLine("  // Choosing Next wallpaper skips black until the configured window ends.");
            text.AppendLine("  // Local 24-hour clock. Start is inclusive; end is exclusive. Defaults: midnight to 7 AM.");
            text.AppendLine("  \"BlackBackgroundStartHour\": " + BlackBackgroundStartHour + ",");
            text.AppendLine("  \"BlackBackgroundEndHour\": " + BlackBackgroundEndHour);
            text.AppendLine("}");
            File.WriteAllText(path, text.ToString());
        }

        private static string RemoveComments(string json)
        {
            StringBuilder result = new StringBuilder(json.Length);
            bool inString = false;
            bool escaped = false;
            for (int i = 0; i < json.Length; i++)
            {
                char current = json[i];
                if (inString)
                {
                    result.Append(current);
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"') { inString = true; result.Append(current); continue; }
                if (current == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    i += 2;
                    while (i < json.Length && json[i] != '\r' && json[i] != '\n') i++;
                    if (i < json.Length) result.Append(json[i]);
                    continue;
                }
                if (current == '/' && i + 1 < json.Length && json[i + 1] == '*')
                {
                    i += 2;
                    bool closed = false;
                    while (i < json.Length)
                    {
                        if (json[i] == '*' && i + 1 < json.Length && json[i + 1] == '/') { i++; closed = true; break; }
                        if (json[i] == '\r' || json[i] == '\n') result.Append(json[i]);
                        i++;
                    }
                    if (!closed) throw new InvalidDataException("config.jsonc contains an unterminated block comment.");
                    continue;
                }
                result.Append(current);
            }
            return result.ToString();
        }

        public bool Accepts(int width, int height)
        {
            if (width < Width || height < Height) return false;
            double targetRatio = (double)Width / Height;
            double difference = Math.Abs(((double)width / height) / targetRatio - 1.0);
            return difference <= AspectRatioTolerance + 0.000000001;
        }
    }

    public sealed class AppLog
    {
        private readonly string path;
        private readonly object gate = new object();

        public AppLog(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            path = Path.Combine(root, "data", "app.log");
        }

        public void Write(string message)
        {
            // A temporarily locked log should not interrupt wallpaper rotation.
            try
            {
                lock (gate) File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
