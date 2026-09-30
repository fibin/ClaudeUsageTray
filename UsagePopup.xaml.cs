using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClaudeUsageTray
{
    /// <param name="RemainingPercent">null = no data (bar shown empty, "—").</param>
    public sealed record BarModel(double? RemainingPercent, string SubText, System.Drawing.Color Color);

    public sealed record PopupModel(BarModel Session, BarModel Week, string Footer, bool FooterIsWarning);

    public partial class UsagePopup : Window
    {
        private static readonly Brush FooterNormal = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        private static readonly Brush FooterWarning = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18));

        public UsagePopup()
        {
            InitializeComponent();

            // Never take focus and never show up in Alt+Tab.
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            };
        }

        public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

        public void Apply(PopupModel model)
        {
            ApplyBar(model.Session, SessionPercent, SessionFill, SessionFillColumn, SessionRestColumn, SessionReset);
            ApplyBar(model.Week, WeekPercent, WeekFill, WeekFillColumn, WeekRestColumn, WeekReset);

            Footer.Text = model.Footer;
            Footer.Foreground = model.FooterIsWarning ? FooterWarning : FooterNormal;
        }

        private static void ApplyBar(BarModel bar, TextBlock percent, Border fill,
            ColumnDefinition fillColumn, ColumnDefinition restColumn, TextBlock sub)
        {
            double remaining = Math.Clamp(bar.RemainingPercent ?? 0, 0, 100);

            percent.Text = bar.RemainingPercent is null ? "—" : $"осталось {Math.Round(remaining):0}%";
            fillColumn.Width = new GridLength(remaining, GridUnitType.Star);
            restColumn.Width = new GridLength(100 - remaining, GridUnitType.Star);

            var c = bar.Color;
            fill.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            fill.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;

            sub.Text = bar.SubText;
            sub.Visibility = string.IsNullOrEmpty(bar.SubText) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
