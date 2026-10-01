using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
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
        // A missing/expired local token costs no network call, so it is re-checked often.
        private static readonly TimeSpan LocalTokenRetry = TimeSpan.FromSeconds(30);
        // Network errors (e.g. right after Windows starts) retry fast: 15 s, 30 s, 60 s ... up to the poll interval.
        private static readonly TimeSpan FirstNetworkRetry = TimeSpan.FromSeconds(15);
        // Event-triggered refreshes (token file changed, resume, network back) wait this long to coalesce...
        private static readonly TimeSpan EventDebounce = TimeSpan.FromSeconds(2);
        // ...and never come closer than this to the previous server request.
        private static readonly TimeSpan MinEventGap = TimeSpan.FromSeconds(10);

        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ToolStripMenuItem _autostartItem;
        private readonly UsageClient _client = new();
        private readonly DispatcherTimer _pollTimer = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _hoverTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly UsagePopup _popup = new();
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private FileSystemWatcher? _credentialsWatcher;
        private FileSystemWatcher? _settingsWatcher;
        private bool _disposed;

        private AppSettings _settings = AppSettings.Load();
        private UsageSnapshot? _last;
        private string? _error;
        private UsageErrorKind? _errorKind;
        private int _rateLimitStreak;
        private int _networkErrorStreak;
        private bool _refreshing;
        private bool _refreshPending;
        private DateTimeOffset _nextDue = DateTimeOffset.MaxValue;
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
            menu.Items.Add("Open log", null, (_, _) => OpenLog());
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
                _nextDue = DateTimeOffset.MaxValue;
                await RefreshAsync();
            };
            _hoverTimer.Tick += (_, _) => CheckHover();
        }

        public void Start()
        {
            Log.Write($"Started v{typeof(TrayController).Assembly.GetName().Version}, autostart={SafeIsAutostart()}");

            UpdateVisuals();
            _notifyIcon.Visible = true;

            EnsureWatchers();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            _ = RefreshAsync();
        }

        // ---------------------------------------------------------------- automatic refresh triggers

        /// <summary>
        /// Watches Claude Code's login file (rewritten whenever Claude Code renews its token) and our
        /// settings.json, so a new token is picked up within seconds instead of at the next poll.
        /// Called on every refresh, so a watcher appears as soon as its folder exists.
        /// </summary>
        private void EnsureWatchers()
        {
            _credentialsWatcher ??= TryWatch(CredentialStore.CredentialsPath, "Claude Code login file changed");
            _settingsWatcher ??= TryWatch(AppSettings.FilePath, "settings.json changed");
        }

        private FileSystemWatcher? TryWatch(string filePath, string reason)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                var watcher = new FileSystemWatcher(dir, Path.GetFileName(filePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };

                FileSystemEventHandler onChange = (_, _) => OnBackgroundEvent(reason, tokenMayHaveChanged: true);
                watcher.Changed += onChange;
                watcher.Created += onChange;
                watcher.Renamed += (_, _) => OnBackgroundEvent(reason, tokenMayHaveChanged: true);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex)
            {
                Log.Write($"Cannot watch {filePath}: {ex.Message}");
                return null;
            }
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                OnBackgroundEvent("resumed from sleep", tokenMayHaveChanged: false);
            }
        }

        private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                OnBackgroundEvent("session unlocked", tokenMayHaveChanged: false);
            }
        }

        private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable)
            {
                OnBackgroundEvent("network available", tokenMayHaveChanged: false);
            }
        }

        /// <summary>File watcher, SystemEvents and NetworkChange fire on other threads — hop to the UI thread.</summary>
        private void OnBackgroundEvent(string reason, bool tokenMayHaveChanged)
        {
            if (_disposed) return;
            _dispatcher.InvokeAsync(() => RequestRefresh(reason, tokenMayHaveChanged));
        }

        private void RequestRefresh(string reason, bool tokenMayHaveChanged)
        {
            if (_disposed) return;

            // While rate-limited, only a new token is worth an early request.
            if (_errorKind == UsageErrorKind.RateLimited && !tokenMayHaveChanged) return;

            // Nothing to do if the data is fresh and healthy and the token didn't change.
            if (_error is null && _last is not null && !tokenMayHaveChanged
                && DateTimeOffset.Now - _last.FetchedAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            if (_refreshing)
            {
                _refreshPending = true;
                return;
            }

            var delay = EventDebounce;
            var sinceLast = DateTimeOffset.Now - _lastAttempt;
            if (sinceLast < MinEventGap && MinEventGap - sinceLast > delay)
            {
                delay = MinEventGap - sinceLast;
            }

            if (DateTimeOffset.Now + delay < _nextDue)
            {
                Log.Write($"Refresh scheduled in {delay.TotalSeconds:0} s: {reason}");
                ScheduleNext(delay);
            }
        }

        private void ScheduleNext(TimeSpan delay)
        {
            if (_disposed) return;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);

            _pollTimer.Stop();
            _pollTimer.Interval = delay;
            _nextDue = DateTimeOffset.Now + delay;
            _pollTimer.Start();
        }

        // ---------------------------------------------------------------- polling

        private async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            _lastAttempt = DateTimeOffset.Now;
            _pollTimer.Stop();

            _settings = AppSettings.Load();
            EnsureWatchers();
            TimeSpan poll = TimeSpan.FromMinutes(_settings.PollIntervalMinutes);
            TimeSpan next = poll;
            string? previousError = _error;

            try
            {
                var token = CredentialStore.Load(_settings);
                _last = await _client.FetchAsync(_settings.UsageUrl, token.AccessToken);
                _error = null;
                _errorKind = null;
                _rateLimitStreak = 0;
                _networkErrorStreak = 0;
            }
            catch (UsageException ex) when (ex.Kind == UsageErrorKind.RateLimited)
            {
                _rateLimitStreak++;
                var backoff = ex.RetryAfter is { } ra && ra > TimeSpan.Zero
                    ? ra
                    : TimeSpan.FromTicks(poll.Ticks * (1L << Math.Min(_rateLimitStreak, 4)));
                if (backoff > MaxBackoff) backoff = MaxBackoff;
                if (backoff < poll) backoff = poll;
                next = backoff;
                _errorKind = ex.Kind;
                _error = $"{ex.Message} Retrying in {FormatSpan(next)}.";
            }
            catch (UsageException ex)
            {
                _errorKind = ex.Kind;
                _error = ex.Message;
                next = RetryDelayFor(ex.Kind, poll);
            }
            catch (Exception ex)
            {
                _errorKind = UsageErrorKind.Network;
                _error = "Error: " + ex.Message;
                next = RetryDelayFor(UsageErrorKind.Network, poll);
            }
            finally
            {
                _refreshing = false;

                if (_error != previousError)
                {
                    Log.Write(_error is null
                        ? $"OK: session {RemainingOf(_last?.FiveHour):0}% left, week {RemainingOf(_last?.SevenDay):0}% left"
                        : $"Error ({_errorKind}): {_error} Next try in {FormatSpan(next)}.");
                }

                if (_refreshPending)
                {
                    _refreshPending = false;
                    if (next > MinEventGap) next = MinEventGap;
                }

                UpdateVisuals();
                ScheduleNext(next);
            }
        }

        private TimeSpan RetryDelayFor(UsageErrorKind kind, TimeSpan poll)
        {
            TimeSpan delay;
            switch (kind)
            {
                case UsageErrorKind.NoToken:
                case UsageErrorKind.TokenExpired:
                    // Only a local file read; the file watcher usually catches the change even sooner.
                    delay = LocalTokenRetry;
                    break;

                case UsageErrorKind.Network:
                    _networkErrorStreak++;
                    delay = TimeSpan.FromTicks(FirstNetworkRetry.Ticks * (1L << Math.Min(_networkErrorStreak - 1, 6)));
                    break;

                default:
                    delay = poll;
                    break;
            }

            if (kind != UsageErrorKind.Network) _networkErrorStreak = 0;
            return delay < poll ? delay : poll;
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

        private static void OpenLog()
        {
            if (!File.Exists(Log.FilePath))
            {
                Log.Write("Log opened");
            }
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true });
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
            if (_disposed) return;
            _disposed = true;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _credentialsWatcher?.Dispose();
            _settingsWatcher?.Dispose();

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
