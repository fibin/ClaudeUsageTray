using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeUsageTray
{
    /// <summary>Owns the tray icon, the polling loop and the hover popup.</summary>
    public sealed class TrayController : IDisposable
    {
        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

        // Hide the popup once the cursor is this far (physical px) from where it last touched the icon.
        private const int HoverLeaveDistance = 28;
        // Data older than this with a failing refresh is shown as gray.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
        // Clicking the icon must not hammer the (rate-limited) endpoint.
        private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromSeconds(30);

        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ToolStripMenuItem _autostartItem;
        private readonly UsageClient _client = new();
        private readonly DispatcherTimer _pollTimer = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _hoverTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly UsagePopup _popup = new();

        private AppSettings _settings = AppSettings.Load();
        private UsageSnapshot? _last;
        private string? _error;
        private int _rateLimitStreak;
        private bool _refreshing;
        private bool _menuOpen;
        private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
        private IntPtr _iconHandle = IntPtr.Zero;
        private Drawing.Point _hoverAnchor;

        public TrayController()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
            menu.Items.Add("Open usage on claude.ai", null, (_, _) => OpenShell("https://claude.ai/settings/usage"));
            menu.Items.Add("Settings (settings.json)", null, (_, _) => OpenSettings());
            menu.Items.Add(new Forms.ToolStripSeparator());

            _autostartItem = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
            _autostartItem.Click += (_, _) => ToggleAutostart();
            menu.Items.Add(_autostartItem);
            menu.Opening += (_, _) =>
            {
                _menuOpen = true;
                HidePopup();
                _autostartItem.Checked = SafeIsAutostart();
            };
            menu.Closed += (_, _) => _menuOpen = false;

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Exit());

            _notifyIcon = new Forms.NotifyIcon
            {
                ContextMenuStrip = menu,
                // Empty text: the native tooltip would cover our own popup.
                Text = string.Empty,
            };
            _notifyIcon.MouseMove += OnIconMouseMove;
            _notifyIcon.MouseClick += async (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left
                    && DateTimeOffset.Now - _lastAttempt > ManualRefreshCooldown)
                {
                    await RefreshAsync();
                }
            };

            _pollTimer.Tick += async (_, _) =>
            {
                _pollTimer.Stop();
                await RefreshAsync();
            };
            _hoverTimer.Tick += (_, _) => CheckHover();
        }

        public void Start()
        {
            UpdateVisuals();
            _notifyIcon.Visible = true;
            _ = RefreshAsync();
        }

        // ---------------------------------------------------------------- polling

        private async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            _lastAttempt = DateTimeOffset.Now;
            _pollTimer.Stop();

            _settings = AppSettings.Load();
            TimeSpan next = TimeSpan.FromMinutes(_settings.PollIntervalMinutes);

            try
            {
                var token = CredentialStore.Load(_settings);
                _last = await _client.FetchAsync(_settings.UsageUrl, token.AccessToken);
                _error = null;
                _rateLimitStreak = 0;
            }
            catch (UsageException ex) when (ex.Kind == UsageErrorKind.RateLimited)
            {
                _rateLimitStreak++;
                var backoff = ex.RetryAfter is { } ra && ra > TimeSpan.Zero
                    ? ra
                    : TimeSpan.FromTicks(next.Ticks * (1L << Math.Min(_rateLimitStreak, 4)));
                if (backoff > MaxBackoff) backoff = MaxBackoff;
                if (backoff < next) backoff = next;
                next = backoff;
                _error = $"{ex.Message} Retrying in {FormatSpan(next)}.";
            }
            catch (UsageException ex)
            {
                _error = ex.Message;
            }
            catch (Exception ex)
            {
                _error = "Error: " + ex.Message;
            }
            finally
            {
                _refreshing = false;
                UpdateVisuals();
                _pollTimer.Interval = next;
                _pollTimer.Start();
            }
        }

        // ---------------------------------------------------------------- visuals

        private void UpdateVisuals()
        {
            bool stale = _last is null || (_error is not null && DateTimeOffset.Now - _last.FetchedAt > StaleAfter);

            double? sessionRemaining = _last is null ? null : RemainingOf(_last.FiveHour);
            var iconColor = stale || sessionRemaining is null
                ? Palette.Gray
                : Palette.ForRemaining(sessionRemaining.Value, _settings);

            int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            var (icon, handle) = IconRenderer.RenderIcon(sessionRemaining, iconColor, size);

            var oldIcon = _notifyIcon.Icon;
            var oldHandle = _iconHandle;
            _notifyIcon.Icon = icon;
            _iconHandle = handle;
            oldIcon?.Dispose();
            if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);

            _popup.Apply(BuildPopupModel(stale));
            if (_popup.IsVisible)
            {
                PositionPopup();
            }
        }

        private PopupModel BuildPopupModel(bool stale)
        {
            BarModel Bar(UsageWindow? window, bool isSession)
            {
                if (_last is null)
                {
                    return new BarModel(null, string.Empty, Palette.Gray);
                }

                double remaining = RemainingOf(window);
                var color = stale ? Palette.Gray : Palette.ForRemaining(remaining, _settings);
                string sub = window is null
                    ? (isSession ? "window not started yet" : string.Empty)
                    : FormatReset(window.ResetsAt);
                return new BarModel(remaining, sub, color);
            }

            string footer;
            bool warning;
            if (_error is not null)
            {
                footer = _last is null
                    ? _error
                    : $"{_error}\nShowing data from {_last.FetchedAt.ToLocalTime():HH:mm}.";
                warning = true;
            }
            else if (_last is not null)
            {
                footer = $"Updated at {_last.FetchedAt.ToLocalTime():HH:mm} · click to refresh";
                warning = false;
            }
            else
            {
                footer = "Loading…";
                warning = false;
            }

            return new PopupModel(Bar(_last?.FiveHour, true), Bar(_last?.SevenDay, false), footer, warning);
        }

        private static double RemainingOf(UsageWindow? window) => window?.RemainingPercent ?? 100.0;

        private static string FormatReset(DateTimeOffset? resetsAt)
        {
            if (resetsAt is null) return string.Empty;

            var local = resetsAt.Value.ToLocalTime();
            var left = local - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero) return "resets any moment";
            if (left < TimeSpan.FromHours(24)) return $"resets in {FormatSpan(left)}";
            return "resets " + local.ToString("ddd HH:mm", Culture);
        }

        private static string FormatSpan(TimeSpan span)
        {
            int totalMinutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
            int h = totalMinutes / 60;
            int m = totalMinutes % 60;
            if (h == 0) return $"{m} min";
            if (m == 0) return $"{h} h";
            return $"{h} h {m} min";
        }

        // ---------------------------------------------------------------- hover popup

        private void OnIconMouseMove(object? sender, Forms.MouseEventArgs e)
        {
            _hoverAnchor = Forms.Cursor.Position;
            if (!_popup.IsVisible && !_menuOpen)
            {
                ShowPopup();
            }
        }

        private void ShowPopup()
        {
            _popup.Opacity = 0;
            _popup.Show();
            PositionPopup();
            _popup.Opacity = 1;
            _hoverTimer.Start();
        }

        private void HidePopup()
        {
            _hoverTimer.Stop();
            if (_popup.IsVisible)
            {
                _popup.Hide();
            }
        }

        private void PositionPopup()
        {
            _popup.UpdateLayout();
            var hwnd = _popup.Handle;
            if (!Native.GetWindowRect(hwnd, out var r)) return;

            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            var anchor = _hoverAnchor;
            var wa = Forms.Screen.FromPoint(anchor).WorkingArea;

            // Default: taskbar at the bottom — popup sits above the cursor.
            int x = anchor.X - w / 2;
            int y = anchor.Y - h - 4;

            if (anchor.Y < wa.Top)
            {
                y = wa.Top;                    // taskbar at the top
            }
            else if (anchor.X >= wa.Right)
            {
                x = wa.Right - w;              // taskbar on the right
                y = anchor.Y - h / 2;
            }
            else if (anchor.X < wa.Left)
            {
                x = wa.Left;                   // taskbar on the left
                y = anchor.Y - h / 2;
            }

            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));

            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private void CheckHover()
        {
            if (!_popup.IsVisible)
            {
                _hoverTimer.Stop();
                return;
            }

            var p = Forms.Cursor.Position;
            if (Native.GetWindowRect(_popup.Handle, out var r)
                && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
            {
                return; // over the popup itself
            }

            int dx = p.X - _hoverAnchor.X;
            int dy = p.Y - _hoverAnchor.Y;
            if (dx * dx + dy * dy > HoverLeaveDistance * HoverLeaveDistance)
            {
                HidePopup();
            }
        }

        // ---------------------------------------------------------------- menu actions

        private static void OpenShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Forms.MessageBox.Show(ex.Message, "Claude Usage Tray", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
        }

        private void OpenSettings()
        {
            if (!System.IO.File.Exists(AppSettings.FilePath))
            {
                _settings.Save();
            }
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Forms.MessageBox.Show(ex.Message, "Claude Usage Tray", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
        }

        private void ToggleAutostart()
        {
            try
            {
                Autostart.Set(_autostartItem.Checked);
            }
            catch (Exception ex)
            {
                _autostartItem.Checked = SafeIsAutostart();
                Forms.MessageBox.Show("Could not change autostart: " + ex.Message, "Claude Usage Tray",
                    Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
        }

        private static bool SafeIsAutostart()
        {
            try { return Autostart.IsEnabled(); }
            catch { return false; }
        }

        private void Exit()
        {
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            _pollTimer.Stop();
            _hoverTimer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Icon = null;
            _notifyIcon.Dispose();
            if (_iconHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
            _popup.Close();
            _client.Dispose();
        }
    }
}
