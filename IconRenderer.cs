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

    /// <summary>Draws the tray "stack": a column of segments filled from the bottom.</summary>
    public static class IconRenderer
    {
        /// <param name="remainingPercent">0..100, or null when there is no data yet (empty gray stack).</param>
        public static Bitmap RenderBitmap(double? remainingPercent, Color color, int size)
        {
            size = Math.Max(16, size);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.Clear(Color.Transparent);

            // Outer body: dark fill + light frame reads on both light and dark taskbars.
            int w = Math.Max(9, (int)Math.Round(size * 0.56));
            if ((size - w) % 2 != 0) w++;
            int x0 = (size - w) / 2;
            int y0 = (int)Math.Round(size * 0.03);
            int h = size - 2 * y0;

            using (var body = new SolidBrush(Color.FromArgb(215, 22, 22, 22)))
            {
                g.FillRectangle(body, x0, y0, w, h);
            }
            using (var frame = new Pen(Color.FromArgb(235, 225, 225, 225)))
            {
                g.DrawRectangle(frame, x0, y0, w - 1, h - 1);
            }

            // Inner area: 1px frame + 1px gap on every side.
            int ix = x0 + 2;
            int iy = y0 + 2;
            int iw = w - 4;
            int ih = h - 4;

            int n = size <= 20 ? 4 : 5;
            int gap = size < 32 ? 1 : 2;
            int[] heights = SplitHeights(ih - gap * (n - 1), n);

            double level = Math.Clamp((remainingPercent ?? 0) / 100.0, 0.0, 1.0);
            var fillColor = remainingPercent is null ? Palette.Gray : color;

            using var emptyBrush = new SolidBrush(Color.FromArgb(55, fillColor));
            using var fillBrush = new SolidBrush(fillColor);
            using var shineBrush = new SolidBrush(Blend(fillColor, Color.White, 0.40));

            // Segments are laid out bottom-up: index 0 is the bottom one.
            int bottom = iy + ih;
            for (int i = 0; i < n; i++)
            {
                int sh = heights[i];
                int top = bottom - sh;

                g.FillRectangle(emptyBrush, ix, top, iw, sh);

                double frac = Math.Clamp(level * n - i, 0.0, 1.0);
                if (frac > 0)
                {
                    // Even a nearly-empty stack shows a 1px sliver so "almost out" is visible.
                    int filled = Math.Max(1, (int)Math.Round(sh * frac));
                    g.FillRectangle(fillBrush, ix, bottom - filled, iw, filled);

                    // A lighter top edge on each filled block gives the "glow".
                    if (filled >= 3)
                    {
                        g.FillRectangle(shineBrush, ix, bottom - filled, iw, 1);
                    }
                }

                bottom = top - gap;
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

        /// <summary>Splits total pixels into n near-equal parts; the extra pixels go to the lower segments.</summary>
        private static int[] SplitHeights(int total, int n)
        {
            var result = new int[n];
            int baseH = Math.Max(1, total / n);
            int rest = Math.Max(0, total - baseH * n);
            for (int i = 0; i < n; i++)
            {
                result[i] = baseH + (i < rest ? 1 : 0);
            }
            return result;
        }

        private static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb(
                a.A,
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }
}
