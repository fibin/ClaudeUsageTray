using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageTray
{
    /// <summary>One limit window. Utilization is "percent used", 0..100.</summary>
    public sealed record UsageWindow(double UtilizationPercent, DateTimeOffset? ResetsAt)
    {
        public double RemainingPercent => Math.Clamp(100.0 - UtilizationPercent, 0.0, 100.0);
    }

    /// <param name="FiveHour">null = no session window running yet (nothing used).</param>
    public sealed record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? SevenDay, DateTimeOffset FetchedAt);

    public enum UsageErrorKind
    {
        NoToken,
        TokenExpired,
        Unauthorized,
        RateLimited,
        Network,
        BadResponse,
    }

    public sealed class UsageException : Exception
    {
        public UsageErrorKind Kind { get; }
        public TimeSpan? RetryAfter { get; }

        public UsageException(UsageErrorKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
            RetryAfter = retryAfter;
        }
    }

    public sealed record TokenInfo(string AccessToken, DateTimeOffset? ExpiresAt, string Source);

    /// <summary>Finds the OAuth token. Read-only: it never refreshes tokens, because Claude Code rotates them.</summary>
    public static class CredentialStore
    {
        public static TokenInfo Load(AppSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.OauthToken))
            {
                return new TokenInfo(settings.OauthToken.Trim(), null, "settings.json");
            }

            var env = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return new TokenInfo(env.Trim(), null, "CLAUDE_CODE_OAUTH_TOKEN");
            }

            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
            {
                configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }

            var path = Path.Combine(configDir, ".credentials.json");
            if (!File.Exists(path))
            {
                throw new UsageException(UsageErrorKind.NoToken,
                    "Не найден вход Claude Code. Войдите в Claude Code или укажите токен в настройках.");
            }

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);

                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                {
                    throw new UsageException(UsageErrorKind.NoToken,
                        "В файле входа Claude Code нет OAuth-токена подписки.");
                }

                string? token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                DateTimeOffset? expiresAt = null;
                if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms))
                {
                    expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new UsageException(UsageErrorKind.NoToken,
                        "В файле входа Claude Code нет OAuth-токена подписки.");
                }

                if (expiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
                {
                    throw new UsageException(UsageErrorKind.TokenExpired,
                        "Токен Claude Code истёк. Запустите Claude Code (он обновит вход) или укажите долгоживущий токен в настройках.");
                }

                return new TokenInfo(token, expiresAt, path);
            }
            catch (UsageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                throw new UsageException(UsageErrorKind.NoToken, "Не удалось прочитать файл входа Claude Code.", null, ex);
            }
        }
    }

    public sealed class UsageClient : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public async Task<UsageSnapshot> FetchAsync(string url, string accessToken, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-usage-tray/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new UsageException(UsageErrorKind.Network, "Нет связи с сервером.", null, ex);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new UsageException(UsageErrorKind.Unauthorized,
                        $"Сервер отклонил токен ({(int)response.StatusCode}). Перезайдите в Claude Code или обновите токен.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    TimeSpan? retry = response.Headers.RetryAfter?.Delta;
                    if (retry is null && response.Headers.RetryAfter?.Date is { } date)
                    {
                        retry = date - DateTimeOffset.UtcNow;
                    }
                    throw new UsageException(UsageErrorKind.RateLimited, "Сервер просит подождать (429).", retry);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new UsageException(UsageErrorKind.Network, $"Сервер ответил {(int)response.StatusCode}.");
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(json);
            }
        }

        /// <summary>
        /// Expected shape (undocumented):
        /// { "five_hour": { "utilization": 12.0, "resets_at": "2026-09-30T22:00:00Z" } | null,
        ///   "seven_day": { "utilization": 45.0, "resets_at": "..." } | null, ... }
        /// </summary>
        public static UsageSnapshot Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new UsageException(UsageErrorKind.BadResponse, "Неожиданный ответ сервера.");
                }

                bool hasFive = root.TryGetProperty("five_hour", out var five);
                bool hasSeven = root.TryGetProperty("seven_day", out var seven);
                if (!hasFive && !hasSeven)
                {
                    throw new UsageException(UsageErrorKind.BadResponse,
                        "В ответе нет данных о лимитах — возможно, формат API изменился.");
                }

                return new UsageSnapshot(
                    hasFive ? ParseWindow(five) : null,
                    hasSeven ? ParseWindow(seven) : null,
                    DateTimeOffset.Now);
            }
            catch (JsonException ex)
            {
                throw new UsageException(UsageErrorKind.BadResponse, "Сервер вернул не JSON.", null, ex);
            }
        }

        private static UsageWindow? ParseWindow(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double utilization = 0;
            if (el.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
            {
                utilization = u.GetDouble();
            }

            DateTimeOffset? resetsAt = null;
            if (el.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                resetsAt = parsed;
            }

            return new UsageWindow(utilization, resetsAt);
        }

        public void Dispose() => _http.Dispose();
    }
}
