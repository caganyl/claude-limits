// Mirrors core/src/credentials.ts for Windows/Linux (no macOS keychain: Visual Studio for Mac is retired).
//   %USERPROFILE%\.claude\.credentials.json  (or %CLAUDE_CONFIG_DIR%\.credentials.json)
// The token is only read, never refreshed or written: refreshing here would rotate the refresh
// token underneath Claude Code and could log it out.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    public sealed class OAuthCredentials
    {
        public string AccessToken { get; set; } = "";

        /// <summary>Epoch ms.</summary>
        public double? ExpiresAt { get; set; }

        public string? Plan { get; set; }

        /// <summary>Human-readable origin, shown in diagnostics.</summary>
        public string Source { get; set; } = "";
    }

    public sealed class CredentialOptions
    {
        /// <summary>Token pasted by the user in settings; wins over everything else.</summary>
        public string? ManualToken { get; set; }

        /// <summary>Override for ~/.claude (otherwise %CLAUDE_CONFIG_DIR% or ~/.claude).</summary>
        public string? ConfigDir { get; set; }

        /// <summary>Environment lookup (test seam); defaults to Environment.GetEnvironmentVariable.</summary>
        public Func<string, string?>? GetEnv { get; set; }

        /// <summary>Home directory (test seam).</summary>
        public string? HomeDir { get; set; }

        public DateTimeOffset? Now { get; set; }
    }

    public static class CredentialReader
    {
        public static string HomeDirectory()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home ?? "";
        }

        public static string ClaudeConfigDir(Func<string, string?>? getEnv = null, string? homeDir = null)
        {
            getEnv = getEnv ?? new Func<string, string?>(Environment.GetEnvironmentVariable);
            var fromEnv = getEnv("CLAUDE_CONFIG_DIR")?.Trim();
            return !string.IsNullOrEmpty(fromEnv) ? Path.GetFullPath(fromEnv) : Path.Combine(homeDir ?? HomeDirectory(), ".claude");
        }

        /// <summary>Extracts the token from the credentials JSON blob Claude Code stores.</summary>
        public static OAuthCredentials? ParseCredentialsBlob(string raw, string source)
        {
            JToken json;
            try
            {
                json = JsonUtil.Parse(raw);
            }
            catch (Exception)
            {
                return null;
            }
            if (!(json is JObject root) || !(root["claudeAiOauth"] is JObject oauth)) return null;
            var token = oauth["accessToken"];
            if (token == null || token.Type != JTokenType.String) return null;
            var accessToken = token.Value<string>();
            if (string.IsNullOrEmpty(accessToken)) return null;
            var creds = new OAuthCredentials { AccessToken = accessToken!, Source = source };
            var exp = oauth["expiresAt"];
            if (exp != null && (exp.Type == JTokenType.Integer || exp.Type == JTokenType.Float)) creds.ExpiresAt = exp.Value<double>();
            var sub = oauth["subscriptionType"];
            if (sub != null && sub.Type == JTokenType.String) creds.Plan = sub.Value<string>();
            return creds;
        }

        /// <summary>
        /// Finds a usable OAuth token. Throws LimitsError(NoCredentials) when nothing is found and
        /// LimitsError(Expired) when the stored token has expired (Claude Code refreshes it on next use).
        /// </summary>
        public static OAuthCredentials Read(CredentialOptions? opts = null)
        {
            opts = opts ?? new CredentialOptions();
            var getEnv = opts.GetEnv ?? new Func<string, string?>(Environment.GetEnvironmentVariable);
            var now = opts.Now ?? DateTimeOffset.UtcNow;

            var manual = opts.ManualToken?.Trim();
            if (!string.IsNullOrEmpty(manual)) return new OAuthCredentials { AccessToken = manual!, Source = "ayarlar (elle girilen token)" };
            var envToken = getEnv("CLAUDE_CODE_OAUTH_TOKEN")?.Trim();
            if (!string.IsNullOrEmpty(envToken)) return new OAuthCredentials { AccessToken = envToken!, Source = "CLAUDE_CODE_OAUTH_TOKEN" };

            string dir;
            if (!string.IsNullOrEmpty(opts.ConfigDir)) dir = opts.ConfigDir!;
            else dir = ClaudeConfigDir(getEnv, opts.HomeDir);

            var candidates = new List<OAuthCredentials>();
            var file = Path.Combine(dir, ".credentials.json");
            try
            {
                if (File.Exists(file))
                {
                    var c = ParseCredentialsBlob(ReadShared(file), file);
                    if (c != null) candidates.Add(c);
                }
            }
            catch (Exception)
            {
                /* missing or locked file is fine */
            }

            if (candidates.Count == 0)
            {
                throw new LimitsError(
                    LimitsErrorKind.NoCredentials,
                    "Claude Code girişi bulunamadı. Terminalde `claude` çalıştırıp /login ile giriş yapın (veya ayarlardan token girin).");
            }
            // Prefer a non-expired token, then the one that expires last.
            var best = candidates.OrderByDescending(c => c.ExpiresAt ?? double.PositiveInfinity).First();
            if (best.ExpiresAt != null && best.ExpiresAt.Value < now.ToUnixTimeMilliseconds())
            {
                throw new LimitsError(
                    LimitsErrorKind.Expired,
                    "Claude Code token’ının süresi dolmuş. Claude Code’u (VS Code eklentisi veya terminal) bir kez kullanınca kendiliğinden yenilenir.");
            }
            return best;
        }

        /// <summary>Reads a file while another process (Claude Code) may have it open for writing.</summary>
        internal static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                return sr.ReadToEnd();
            }
        }
    }
}
