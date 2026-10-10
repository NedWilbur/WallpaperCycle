using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WallpaperCycle
{
    public static class WindowsIntegration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
        private static extern bool GetWallpaperPath(uint action, uint parameter, System.Text.StringBuilder value, uint flags);

        public static void SetWallpaper(string path)
        {
            string previousWallpaper = null;
            try { previousWallpaper = GetWallpaper(); }
            catch (Win32Exception) { }
            string windowsPath = ConvertUnsupportedFormat(path);
            // Applying the wallpaper is the important operation. Some managed Windows
            // environments deny these optional style writes even when SPI is allowed.
            try
            {
                using (RegistryKey desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (desktop != null)
                    {
                        desktop.SetValue("WallpaperStyle", "10"); // Fill, preserving aspect ratio.
                        desktop.SetValue("TileWallpaper", "0");
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            if (!SystemParametersInfo(0x0014, 0, windowsPath, 0x0001 | 0x0002))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Windows could not set the wallpaper (SystemParametersInfo error " + error + ").");
            }
            string convertedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperCycle");
            if (Directory.Exists(convertedDirectory))
            {
                foreach (string oldFile in Directory.GetFiles(convertedDirectory, "wallpapercycle-*.jpg").Where(file =>
                    !string.Equals(file, windowsPath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file, previousWallpaper, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(oldFile); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static string SetBlackBackground()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperCycle");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "black-background.bmp");
            if (!File.Exists(path))
            {
                using (Bitmap image = new Bitmap(1, 1, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Black);
                    image.Save(path, ImageFormat.Bmp);
                }
            }
            SetWallpaper(path);
            return path;
        }

        private static string ConvertUnsupportedFormat(string path)
        {
            string extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)) return path;

            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperCycle");
            Directory.CreateDirectory(directory);
            string converted = Path.Combine(directory, "wallpapercycle-" + Guid.NewGuid().ToString("N") + ".jpg");
            using (Image image = Image.FromFile(path)) image.Save(converted, ImageFormat.Jpeg);
            return converted;
        }

        public static string GetWallpaper()
        {
            System.Text.StringBuilder path = new System.Text.StringBuilder(32768);
            if (!GetWallpaperPath(0x0073, (uint)path.Capacity, path, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the current wallpaper.");
            return path.ToString();
        }

        public static bool StartsWithWindows(string executable)
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey))
                return run != null && string.Equals(run.GetValue("WallpaperCycle") as string, Quote(executable), StringComparison.OrdinalIgnoreCase);
        }

        public static void SetStartsWithWindows(string executable, bool enabled)
        {
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) run.SetValue("WallpaperCycle", Quote(executable));
                else run.DeleteValue("WallpaperCycle", false);
            }
        }

        private static string Quote(string value) { return "\"" + value + "\""; }
    }
}
