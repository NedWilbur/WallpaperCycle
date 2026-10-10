using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WallpaperCycle
{
    internal static class TrayIcon
    {
        private const int Size = 32;

        public static Icon Create()
        {
            using (Bitmap bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);

                using (GraphicsPath background = RoundedRectangle(new Rectangle(2, 2, 28, 28), 7))
                using (SolidBrush backgroundBrush = new SolidBrush(Color.FromArgb(255, 25, 43, 68)))
                using (Pen border = new Pen(Color.FromArgb(255, 105, 157, 187), 1.25f))
                {
                    graphics.FillPath(backgroundBrush, background);
                    graphics.DrawPath(border, background);
                }

                using (SolidBrush sun = new SolidBrush(Color.FromArgb(255, 255, 194, 91)))
                    graphics.FillEllipse(sun, 18, 7, 8, 8);

                using (GraphicsPath mountains = new GraphicsPath())
                using (SolidBrush mountainBrush = new SolidBrush(Color.FromArgb(255, 83, 190, 181)))
                {
                    mountains.AddPolygon(new[] {
                        new Point(4, 25), new Point(12, 14), new Point(17, 20),
                        new Point(21, 16), new Point(28, 25)
                    });
                    graphics.FillPath(mountainBrush, mountains);
                }

                using (GraphicsPath foreground = new GraphicsPath())
                using (SolidBrush foregroundBrush = new SolidBrush(Color.FromArgb(255, 89, 133, 185)))
                {
                    foreground.AddPolygon(new[] {
                        new Point(4, 25), new Point(11, 20), new Point(16, 24),
                        new Point(22, 19), new Point(28, 25)
                    });
                    graphics.FillPath(foregroundBrush, foreground);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
