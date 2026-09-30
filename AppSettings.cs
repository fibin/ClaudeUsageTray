using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageTray
{
    /// <summary>
    /// %APPDATA%\ClaudeUsageTray\settings.json — created with defaults on first run,
    /// re-read before every refresh so edits apply without restarting.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>How often to ask the server, in minutes (min 1). The endpoint rate-limits aggressive polling.</summary>
        public int PollIntervalMinutes { get; set; } = 3;

        /// <summary>Remaining % at or below which the indicator turns yellow.</summary>
        public int WarnBelowPercent { get; set; } = 50;

        /// <summary>Remaining % at or below which the indicator turns red.</summary>
        public int CriticalBelowPercent { get; set; } = 20;

        /// <summary>Undocumented usage endpoint used by Claude Code. Kept configurable in case it moves.</summary>
        public string UsageUrl { get; set; } = "https://api.anthropic.com/api/oauth/usage";

        /// <summary>
        /// Optional OAuth token (e.g. from `claude setup-token`). When empty, the token is taken from
        /// the CLAUDE_CODE_OAUTH_TOKEN environment variable or Claude Code's own login file.
        /// </summary>
        public string? OauthToken { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray");

        public static string FilePath => Path.Combine(Directory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    var defaults = new AppSettings();
                    defaults.Save();
                    return defaults;
                }

                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                loaded.Normalize();
                return loaded;
            }
            catch
            {
                // A broken settings file must not kill the tray icon.
                return new AppSettings();
            }
        }

        public void Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }

        private void Normalize()
        {
            if (PollIntervalMinutes < 1) PollIntervalMinutes = 1;
            WarnBelowPercent = Math.Clamp(WarnBelowPercent, 0, 100);
            CriticalBelowPercent = Math.Clamp(CriticalBelowPercent, 0, WarnBelowPercent);
            if (string.IsNullOrWhiteSpace(UsageUrl))
            {
                UsageUrl = new AppSettings().UsageUrl;
            }
        }
    }
}
