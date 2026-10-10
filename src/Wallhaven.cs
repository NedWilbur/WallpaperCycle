using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WallpaperCycle
{
    public static class WallhavenColors
    {
        private static readonly Dictionary<string, string> Palette = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "red", "cc0000" }, { "pink", "ea4c88" }, { "purple", "663399" },
            { "blue", "0066cc" }, { "cyan", "0099cc" }, { "green", "669900" },
            { "yellow", "ffff00" }, { "orange", "ff9900" }, { "brown", "996633" },
            { "black", "000000" }, { "gray", "999999" }, { "white", "ffffff" }
        };

        public static bool IsValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return value.Split(',').All(item => Palette.ContainsKey(item.Trim()) || Regex.IsMatch(item.Trim(), "^[0-9a-fA-F]{6}$"));
        }

        public static string ToHexList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return string.Join(",", value.Split(',').Select(item =>
            {
                string color = item.Trim();
                string hex;
                return Palette.TryGetValue(color, out hex) ? hex : color;
            }));
        }
    }

    public sealed class WallpaperInfo
    {
        public string id { get; set; }
        public string path { get; set; }
        public string url { get; set; }
        public int dimension_x { get; set; }
        public int dimension_y { get; set; }
    }

    public sealed class SearchResponse
    {
        public List<WallpaperInfo> data { get; set; }
        public SearchMetadata meta { get; set; }
    }

    public sealed class SearchMetadata
    {
        public int current_page { get; set; }
        public int last_page { get; set; }
    }

    public sealed class SearchPageState
    {
        public string signature { get; set; }
        public int next_page { get; set; }
    }

    public sealed class Wallhaven
    {
        private readonly HttpClient http;
        private readonly Settings settings;
        private readonly string directory;
        private readonly string pageStatePath;
        private readonly string searchSignature;
        private readonly Random random = new Random();
        private const int MaxDownloadBytes = 60 * 1024 * 1024;
        private int nextPage = 1;

        public Wallhaven(HttpClient http, Settings settings, string directory)
        {
            this.http = http;
            this.settings = settings;
            this.directory = directory;
            string root = Directory.GetParent(Path.GetFullPath(directory)).FullName;
            pageStatePath = Path.Combine(root, "data", "search-page.json");
            searchSignature = CreateSearchSignature(settings);
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(pageStatePath));
            LoadPageState();
        }

        public string SearchUrl()
        {
            return SearchUrl(nextPage);
        }

        private string SearchUrl(int page)
        {
            // Wallhaven groups nearby ultrawide ratios under 21x9. Always check the actual dimensions too.
            double ratio = (double)settings.Width / settings.Height;
            string[] ratios = { "16x9", "16x10", "21x9", "32x9", "48x9" };
            double[] values = { 16.0 / 9, 1.6, 21.0 / 9, 32.0 / 9, 48.0 / 9 };
            int closest = Enumerable.Range(0, ratios.Length).OrderBy(i => Math.Abs(values[i] / ratio - 1)).First();
            string ratioFilter = Math.Abs(values[closest] / ratio - 1) <= settings.AspectRatioTolerance
                ? "&ratios=" + ratios[closest] : "";
            return "https://wallhaven.cc/api/v1/search?sorting=" + Uri.EscapeDataString(settings.Sorting)
                + "&categories=" + settings.Categories
                + "&purity=" + settings.Purity + "&atleast=" + settings.Width.ToString(CultureInfo.InvariantCulture)
                + "x" + settings.Height.ToString(CultureInfo.InvariantCulture) + ratioFilter
                + "&q=" + Uri.EscapeDataString(settings.Query ?? "")
                + (string.IsNullOrEmpty(settings.Colors) ? "" : "&colors=" + Uri.EscapeDataString(WallhavenColors.ToHexList(settings.Colors)))
                + "&page=" + page.ToString(CultureInfo.InvariantCulture);
        }

        public async Task<string> DownloadNewAsync(CancellationToken cancellation)
        {
            if (settings.Purity[2] == '1' && string.IsNullOrWhiteSpace(settings.ApiKey))
                throw new InvalidDataException("Purity enables NSFW results, which require a valid Wallhaven API key.");

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                SearchResponse search;
                int requestedPage = nextPage;
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, SearchUrl(requestedPage)))
                {
                    // Only the API request receives the key; image downloads never receive it.
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) request.Headers.Add("X-API-Key", settings.ApiKey);
                    using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    {
                        EnsureSuccess(response, "Wallhaven search");
                        string json = await ReadJsonAsync(response.Content, timeout.Token);
                        search = new JavaScriptSerializer().Deserialize<SearchResponse>(json);
                    }
                }
                if (search == null || search.data == null) throw new InvalidDataException("Wallhaven returned an invalid search response.");
                int responsePage = search.meta != null && search.meta.current_page > 0 ? search.meta.current_page : requestedPage;
                int lastPage = search.meta != null && search.meta.last_page > 0 ? search.meta.last_page : responsePage;
                List<WallpaperInfo> candidates = search.data.Where(IsNewMatch).OrderBy(w => random.Next()).ToList();
                if (candidates.Count == 0)
                {
                    AdvancePage(responsePage, lastPage);
                    SavePageState();
                    throw new InvalidDataException("No new wallpapers matched the resolution and aspect ratio filters on page " + responsePage + ". The next rotation will check another page.");
                }

                // One download per rotation keeps requests and behavior simple.
                WallpaperInfo wallpaper = candidates[0];
                Uri download = new Uri(wallpaper.path);
                string extension = Path.GetExtension(download.AbsolutePath).ToLowerInvariant();
                string destination = Path.Combine(directory, "wallhaven-" + wallpaper.id + extension);
                string partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using (HttpResponseMessage response = await http.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    {
                        EnsureSuccess(response, "Wallpaper download");
                        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                            throw new InvalidDataException("Wallpaper exceeds the 60 MB download limit.");
                        using (Stream input = await response.Content.ReadAsStreamAsync())
                        using (FileStream output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            int total = 0;
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                            {
                                total += count;
                                if (total > MaxDownloadBytes) throw new InvalidDataException("Wallpaper exceeds the 60 MB download limit.");
                                await output.WriteAsync(buffer, 0, count, timeout.Token);
                            }
                        }
                    }
                    ValidateImage(partial, settings);
                    timeout.Token.ThrowIfCancellationRequested();
                    File.Move(partial, destination);
                    AdvancePage(responsePage, lastPage);
                    SavePageState();
                    return destination;
                }
                finally
                {
                    // Only unfinished downloads are removed. Saved wallpapers are never pruned.
                    if (File.Exists(partial)) File.Delete(partial);
                }
            }
        }

        private void AdvancePage(int currentPage, int lastPage)
        {
            nextPage = currentPage >= lastPage ? 1 : currentPage + 1;
        }

        private void LoadPageState()
        {
            if (!File.Exists(pageStatePath)) return;
            try
            {
                SearchPageState state = new JavaScriptSerializer().Deserialize<SearchPageState>(File.ReadAllText(pageStatePath));
                if (state != null && state.signature == searchSignature && state.next_page > 0)
                    nextPage = state.next_page;
            }
            catch (Exception) { nextPage = 1; }
        }

        private void SavePageState()
        {
            SearchPageState state = new SearchPageState { signature = searchSignature, next_page = nextPage };
            try { File.WriteAllText(pageStatePath, new JavaScriptSerializer().Serialize(state)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string CreateSearchSignature(Settings settings)
        {
            string value = string.Join("|", settings.Sorting, settings.Query, settings.Categories, settings.Purity,
                settings.Width.ToString(CultureInfo.InvariantCulture), settings.Height.ToString(CultureInfo.InvariantCulture),
                settings.AspectRatioTolerance.ToString("R", CultureInfo.InvariantCulture), settings.Colors, settings.ApiKey);
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        private bool IsNewMatch(WallpaperInfo wallpaper)
        {
            if (wallpaper == null || !Regex.IsMatch(wallpaper.id ?? "", "^[a-z0-9]{6}$")) return false;
            if (!settings.Accepts(wallpaper.dimension_x, wallpaper.dimension_y)) return false;
            Uri uri;
            if (!Uri.TryCreate(wallpaper.path, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "w.wallhaven.cc") return false;
            string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension != ".jpg" && extension != ".jpeg" && extension != ".png") return false;
            return !Directory.EnumerateFiles(directory, "wallhaven-" + wallpaper.id + ".*").Any(p => !p.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }

        public static void ValidateImage(string path, Settings settings)
        {
            using (Image image = Image.FromFile(path, true))
            {
                if (!settings.Accepts(image.Width, image.Height))
                    throw new InvalidDataException("Image dimensions do not match the configured resolution and aspect ratio.");
                if ((long)image.Width * image.Height > 100000000)
                    throw new InvalidDataException("Image exceeds the 100 megapixel limit.");
                // Decode the pixels before accepting a download or an offline candidate.
                using (Bitmap decoded = new Bitmap(image)) { }
            }
        }

        private static async Task<string> ReadJsonAsync(HttpContent content, CancellationToken cancellation)
        {
            using (Stream input = await content.ReadAsStreamAsync())
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)) > 0)
                {
                    if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Search response is too large.");
                    output.Write(buffer, 0, count);
                }
                return System.Text.Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static void EnsureSuccess(HttpResponseMessage response, string operation)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(operation + " returned HTTP " + (int)response.StatusCode + " (" + response.StatusCode + ").");
        }
    }
}
