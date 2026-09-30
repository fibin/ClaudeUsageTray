using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ClaudeUsageTray
{
    public static class Palette
    {
        public static readonly Color Green = Color.FromArgb(0x3F, 0xD1, 0x6B);
        public static readonly Color Yellow = Color.FromArgb(0xF5, 0xC5, 0x18);
        public static readonly Color Red = Color.FromArgb(0xEF, 0x4A, 0x3C);
        public static readonly Color Gray = Color.FromArgb(0x8A, 0x8A, 0x8A);

        public static Color ForRemaining(double remainingPercent, AppSettings settings)
        {
            if (remainingPercent > settings.WarnBelowPercent) return Green;
            if (remainingPercent > settings.CriticalBelowPercent) return Yellow;
            return Red;
        }
    }

    /// <summary>Draws the tray icon: a vertical battery with a solid fill rising from the bottom.</summary>
    public static class IconRenderer
    {
        private static readonly Color ShellFill = Color.FromArgb(215, 22, 22, 22);
        private static readonly Color Outline = Color.FromArgb(235, 225, 225, 225);
        // Mid-gray so the terminal cap stays visible on both light and dark taskbars.
        private static readonly Color Cap = Color.FromArgb(235, 170, 170, 170);

        /// <param name="remainingPercent">0..100, or null when there is no data yet (empty gray battery).</param>
        public static Bitmap RenderBitmap(double? remainingPercent, Color color, int size)
        {
            size = Math.Max(16, size);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // ---- geometry (whole pixels so edges stay crisp at 16 px)
            int capH = Math.Max(2, (int)Math.Round(size * 0.10));
            int capW = Math.Max(4, (int)Math.Round(size * 0.34));
            if ((size - capW) % 2 != 0) capW++;

            int bodyW = Math.Max(10, (int)Math.Round(size * 0.62));
            if ((size - bodyW) % 2 != 0) bodyW++;
            int bodyX = (size - bodyW) / 2;
            int bodyY = size < 24 ? capH - 1 : capH;   // at small sizes the cap overlaps the outline by 1 px
            int bodyH = size - bodyY;

            float pen = Math.Max(1, (int)Math.Round(size / 16.0));
            int gap = size < 24 ? 1 : 2;
            float radius = Math.Max(2f, size * 0.16f);

            // ---- terminal cap
            float capX = (size - capW) / 2f;
            using (var capPath = RoundedRect(new RectangleF(capX, 0, capW, capH + 1), 1f))
            using (var capBrush = new SolidBrush(Cap))
            {
                g.FillPath(capBrush, capPath);
            }

            // ---- shell: dark fill + light outline, readable on light and dark taskbars
            var body = new RectangleF(bodyX + pen / 2f, bodyY + pen / 2f, bodyW - pen, bodyH - pen);
            using (var bodyPath = RoundedRect(body, radius))
            using (var shellBrush = new SolidBrush(ShellFill))
            using (var outlinePen = new Pen(Outline, pen))
            {
                g.FillPath(shellBrush, bodyPath);
                g.DrawPath(outlinePen, bodyPath);
            }

            // ---- inner well and the solid charge level
            float inset = pen + gap;
            var inner = new RectangleF(bodyX + inset, bodyY + inset, bodyW - 2 * inset, bodyH - 2 * inset);
            float innerRadius = Math.Max(0.8f, radius - inset);

            var fillColor = remainingPercent is null ? Palette.Gray : color;
            double level = Math.Clamp((remainingPercent ?? 0) / 100.0, 0.0, 1.0);

            using var innerPath = RoundedRect(inner, innerRadius);
            using (var trackBrush = new SolidBrush(Color.FromArgb(45, fillColor)))
            {
                g.FillPath(trackBrush, innerPath);
            }

            if (level > 0)
            {
                // Even a nearly empty battery shows a 1 px sliver so "almost out" stays visible.
                float fillH = Math.Max(1f, (float)(inner.Height * level));
                var state = g.Save();
                g.SetClip(new RectangleF(inner.X - 1, inner.Bottom - fillH, inner.Width + 2, fillH + 1));
                using (var fillBrush = new SolidBrush(fillColor))
                {
                    g.FillPath(fillBrush, innerPath);
                }
                g.Restore(state);
            }

            return bmp;
        }

        /// <summary>Returns an icon plus its native handle; the caller must DestroyIcon the handle when replacing it.</summary>
        public static (Icon Icon, IntPtr Handle) RenderIcon(double? remainingPercent, Color color, int size)
        {
            using var bmp = RenderBitmap(remainingPercent, color, size);
            IntPtr handle = bmp.GetHicon();
            return (Icon.FromHandle(handle), handle);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            if (d <= 0.01f)
            {
                path.AddRectangle(r);
                return path;
            }

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
