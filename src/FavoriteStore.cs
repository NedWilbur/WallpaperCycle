using System;
using System.IO;

namespace WallpaperCycle
{
    public static class FavoriteStore
    {
        public static string Save(string root, string wallpaperPath)
        {
            if (string.IsNullOrWhiteSpace(wallpaperPath) || !File.Exists(wallpaperPath))
                throw new FileNotFoundException("The current wallpaper file could not be found.", wallpaperPath);

            string extension = Path.GetExtension(wallpaperPath).ToLowerInvariant();
            if (extension != ".jpg" && extension != ".jpeg" && extension != ".png" && extension != ".bmp")
                throw new InvalidDataException("The current wallpaper is not a supported image file.");

            string directory = Path.Combine(root, "favorites");
            Directory.CreateDirectory(directory);
            string name = Path.GetFileNameWithoutExtension(wallpaperPath);
            string destination = Path.Combine(directory, name + extension);
            for (int suffix = 2; File.Exists(destination); suffix++)
            {
                if (SameContents(wallpaperPath, destination)) return destination;
                destination = Path.Combine(directory, name + " (" + suffix + ")" + extension);
            }

            File.Copy(wallpaperPath, destination, false);
            return destination;
        }

        private static bool SameContents(string firstPath, string secondPath)
        {
            using (FileStream first = File.OpenRead(firstPath))
            using (FileStream second = File.OpenRead(secondPath))
            {
                if (first.Length != second.Length) return false;
                byte[] firstBuffer = new byte[81920];
                byte[] secondBuffer = new byte[81920];
                int firstCount;
                while ((firstCount = first.Read(firstBuffer, 0, firstBuffer.Length)) > 0)
                {
                    int secondCount = second.Read(secondBuffer, 0, secondBuffer.Length);
                    if (firstCount != secondCount) return false;
                    for (int i = 0; i < firstCount; i++)
                        if (firstBuffer[i] != secondBuffer[i]) return false;
                }
                return second.ReadByte() == -1;
            }
        }
    }
}
