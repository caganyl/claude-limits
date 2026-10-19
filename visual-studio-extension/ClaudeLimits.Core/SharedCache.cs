// Mirrors core/src/cache.ts. The file ~/.claude-limits/cache.json is shared by every Claude Limits
// client on this machine (VS Code windows, tray app, this extension). Format: docs/cache-format.md.
// The document is handled as a JObject so sections/properties this client does not know survive a write.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    public static class CacheSections
    {
        public const string Subscription = "subscription";
        public const string RateLimits = "rateLimits";
        public const string Cost = "cost";
    }

    /// <summary>One section of the cache file. <see cref="Data"/> is kept as raw JSON so unknown fields round-trip.</summary>
    public sealed class CacheEntry
    {
        /// <summary>ISO time of the last attempt (success or failure).</summary>
        public string FetchedAt { get; set; } = "";

        public JToken? Data { get; set; }
        public string? Error { get; set; }
        public string? ErrorKind { get; set; }

        /// <summary>ISO time before which nobody should call the endpoint again (after HTTP 429).</summary>
        public string? BackoffUntil { get; set; }

        /// <summary>Identifies the credential the entry belongs to (hash, never the secret).</summary>
        public string? KeyId { get; set; }

        public bool HasData => Data != null && Data.Type != JTokenType.Null && Data.Type != JTokenType.Undefined;

        public T? DataAs<T>() where T : class
        {
            if (!HasData) return null;
            try
            {
                return JsonUtil.ToObject<T>(Data!);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Same property order as the TypeScript writer: fetchedAt, keyId, data, error, errorKind, backoffUntil.</summary>
        public JObject ToJson()
        {
            var o = new JObject { ["fetchedAt"] = FetchedAt };
            if (KeyId != null) o["keyId"] = KeyId;
            if (HasData) o["data"] = Data!.DeepClone();
            if (Error != null) o["error"] = Error;
            if (ErrorKind != null) o["errorKind"] = ErrorKind;
            if (BackoffUntil != null) o["backoffUntil"] = BackoffUntil;
            return o;
        }

        public static CacheEntry? FromJson(JToken? token)
        {
            if (!(token is JObject o)) return null;
            return new CacheEntry
            {
                FetchedAt = StringOrNull(o["fetchedAt"]) ?? "",
                Data = o["data"]?.DeepClone(),
                Error = StringOrNull(o["error"]),
                ErrorKind = StringOrNull(o["errorKind"]),
                BackoffUntil = StringOrNull(o["backoffUntil"]),
                KeyId = StringOrNull(o["keyId"]),
            };
        }

        private static string? StringOrNull(JToken? t) => t != null && t.Type == JTokenType.String ? t.Value<string>() : null;
    }

    public sealed class SharedCache
    {
        private static readonly object FileLock = new object();

        public SharedCache(string? dir = null)
        {
            Dir = dir ?? DefaultCacheDir();
            FilePath = Path.Combine(Dir, "cache.json");
        }

        public string Dir { get; }
        public string FilePath { get; }

        public static string DefaultCacheDir() => Path.Combine(CredentialReader.HomeDirectory(), ".claude-limits");

        /// <summary>Short, non-reversible id for a secret: lowercase hex sha256 (UTF-8), first 12 chars, plus "|extra".</summary>
        public static string KeyId(string secret, string extra = "")
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(secret));
                var sb = new StringBuilder(64);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString(0, 12) + (string.IsNullOrEmpty(extra) ? "" : "|" + extra);
            }
        }

        /// <summary>Reads the whole document; a missing/corrupt/foreign-version file yields <c>{ "version": 1 }</c>.</summary>
        public JObject Read()
        {
            lock (FileLock)
            {
                return ReadUnlocked();
            }
        }

        private JObject ReadUnlocked()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = JsonUtil.Parse(CredentialReader.ReadShared(FilePath)) as JObject;
                    var v = json?["version"];
                    if (json != null && v != null && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) && v.Value<double>() == 1)
                        return json;
                }
            }
            catch (Exception)
            {
                /* missing or corrupt — start fresh */
            }
            return new JObject { ["version"] = 1 };
        }

        public CacheEntry? Get(string section) => CacheEntry.FromJson(Read()[section]);

        /// <summary>Replaces one section, keeping everything else in the file (unknown sections included).</summary>
        public void Put(string section, CacheEntry entry)
        {
            lock (FileLock)
            {
                var current = ReadUnlocked();
                current[section] = entry.ToJson();
                var text = JsonUtil.Stringify(current);
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + "." + CurrentPid() + "." + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".tmp";
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(tmp, text, utf8);
                try
                {
                    if (File.Exists(FilePath))
                        File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
                    else
                        File.Move(tmp, FilePath);
                }
                catch (Exception)
                {
                    // Windows can refuse a replace over a file another process has open; fall back to a direct write.
                    File.WriteAllText(FilePath, text, utf8);
                    try { File.Delete(tmp); } catch (Exception) { /* ignore */ }
                }
            }
        }

        private static int CurrentPid()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) return p.Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
