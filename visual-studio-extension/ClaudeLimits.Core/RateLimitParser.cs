// Mirrors parseRateLimitHeaders() in core/src/parse.ts.
using System;
using System.Collections.Generic;

namespace ClaudeLimits.Core
{
    public static class RateLimitParser
    {
        private static readonly KeyValuePair<string, string>[] Buckets =
        {
            new KeyValuePair<string, string>("requests", "İstek / dk"),
            new KeyValuePair<string, string>("input_tokens", "Girdi token / dk"),
            new KeyValuePair<string, string>("output_tokens", "Çıktı token / dk"),
            new KeyValuePair<string, string>("tokens", "Toplam token / dk"),
        };

        /// <summary>Builds rate-limit buckets from <c>anthropic-ratelimit-*</c> response headers.</summary>
        /// <param name="getHeader">Case-insensitive header lookup; returns null when missing.</param>
        public static ApiRateLimits Parse(Func<string, string?> getHeader, string model, bool limited, DateTimeOffset? now = null)
        {
            var buckets = new List<RateLimitBucket>();
            foreach (var b in Buckets)
            {
                // JS: b.id.replace('_', '-') replaces only the first underscore.
                var name = ReplaceFirst(b.Key, "_", "-");
                var limit = Js.NumFromString(getHeader("anthropic-ratelimit-" + name + "-limit"));
                var remaining = Js.NumFromString(getHeader("anthropic-ratelimit-" + name + "-remaining"));
                if (limit == null || remaining == null || limit.Value <= 0) continue;
                var resetsAt = getHeader("anthropic-ratelimit-" + name + "-reset");
                var bucket = new RateLimitBucket
                {
                    Id = b.Key,
                    Label = b.Value,
                    Limit = limit.Value,
                    Remaining = remaining.Value,
                    PercentUsed = Js.ClampPct((limit.Value - remaining.Value) / limit.Value * 100),
                };
                if (!string.IsNullOrEmpty(resetsAt)) bucket.ResetsAt = resetsAt;
                buckets.Add(bucket);
            }
            return new ApiRateLimits
            {
                Model = model,
                Buckets = buckets,
                Limited = limited,
                FetchedAt = Iso.Format(now ?? DateTimeOffset.UtcNow),
            };
        }

        private static string ReplaceFirst(string s, string find, string replacement)
        {
            var i = s.IndexOf(find, StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(0, i) + replacement + s.Substring(i + find.Length);
        }
    }
}
