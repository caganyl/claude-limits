// Mirrors core/src/model.ts. Property names are pinned with [JsonProperty] so the shared
// cache file (~/.claude-limits/cache.json) is byte-compatible with the TypeScript clients.
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClaudeLimits.Core
{
    public enum Level
    {
        Unknown,
        Ok,
        Warn,
        Crit,
    }

    public enum DisplayMode
    {
        Session,
        Weekly,
        Both,
        Highest,
    }

    /// <summary>One rolling subscription window (5-hour session, weekly, per-model weekly...).</summary>
    public sealed class LimitWindow
    {
        /// <summary>Stable id: "session", "weekly_all", "weekly_scoped:Opus", or a raw API key.</summary>
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        /// <summary>Long label shown in panels, e.g. "Haftalık · Opus".</summary>
        [JsonProperty("label")]
        public string Label { get; set; } = "";

        /// <summary>Short label for status bars, e.g. "7g Opus".</summary>
        [JsonProperty("shortLabel")]
        public string ShortLabel { get; set; } = "";

        /// <summary>0–100.</summary>
        [JsonProperty("percent")]
        public double Percent { get; set; }

        /// <summary>ISO-8601 reset time, when the API returns one.</summary>
        [JsonProperty("resetsAt", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResetsAt { get; set; }

        /// <summary>True when Anthropic marks this window as the one currently binding the account (null otherwise).</summary>
        [JsonProperty("active", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Active { get; set; }

        [JsonIgnore]
        public bool IsActive => Active == true;

        /// <summary>
        /// True for buckets Anthropic hasn't documented (internal codenames such as "iguana_necktie").
        /// Shown separately; never used for the status bar, colours or notifications.
        /// </summary>
        [JsonProperty("undocumented", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Undocumented { get; set; }

        [JsonIgnore]
        public bool IsUndocumented => Undocumented == true;
    }

    public sealed class ExtraUsage
    {
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        /// <summary>0–100, when known.</summary>
        [JsonProperty("percent", NullValueHandling = NullValueHandling.Ignore)]
        public double? Percent { get; set; }

        /// <summary>Monthly cap in USD, when known.</summary>
        [JsonProperty("monthlyLimitUsd", NullValueHandling = NullValueHandling.Ignore)]
        public double? MonthlyLimitUsd { get; set; }
    }

    public sealed class SubscriptionUsage
    {
        [JsonProperty("windows")]
        public List<LimitWindow> Windows { get; set; } = new List<LimitWindow>();

        [JsonProperty("extra", NullValueHandling = NullValueHandling.Ignore)]
        public ExtraUsage? Extra { get; set; }

        /// <summary>Plan name from the local credentials (e.g. "max", "pro"), when known.</summary>
        [JsonProperty("plan", NullValueHandling = NullValueHandling.Ignore)]
        public string? Plan { get; set; }

        /// <summary>"claude-code" or "claude-web".</summary>
        [JsonProperty("source")]
        public string Source { get; set; } = "claude-code";

        [JsonProperty("fetchedAt")]
        public string FetchedAt { get; set; } = "";
    }

    public sealed class RateLimitBucket
    {
        /// <summary>"requests" | "tokens" | "input_tokens" | "output_tokens".</summary>
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("label")]
        public string Label { get; set; } = "";

        [JsonProperty("limit")]
        public double Limit { get; set; }

        [JsonProperty("remaining")]
        public double Remaining { get; set; }

        /// <summary>0–100, used share of the per-minute allowance.</summary>
        [JsonProperty("percentUsed")]
        public double PercentUsed { get; set; }

        [JsonProperty("resetsAt", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResetsAt { get; set; }
    }

    public sealed class ApiRateLimits
    {
        [JsonProperty("model")]
        public string Model { get; set; } = "";

        [JsonProperty("buckets")]
        public List<RateLimitBucket> Buckets { get; set; } = new List<RateLimitBucket>();

        /// <summary>True when the probe itself was answered with HTTP 429.</summary>
        [JsonProperty("limited")]
        public bool Limited { get; set; }

        [JsonProperty("fetchedAt")]
        public string FetchedAt { get; set; } = "";
    }

    public sealed class TokenTotals
    {
        [JsonProperty("uncachedInput")]
        public double UncachedInput { get; set; }

        [JsonProperty("cacheRead")]
        public double CacheRead { get; set; }

        [JsonProperty("cacheWrite")]
        public double CacheWrite { get; set; }

        [JsonProperty("output")]
        public double Output { get; set; }

        public void Add(TokenTotals other)
        {
            UncachedInput += other.UncachedInput;
            CacheRead += other.CacheRead;
            CacheWrite += other.CacheWrite;
            Output += other.Output;
        }
    }

    public sealed class ApiCost
    {
        [JsonProperty("monthToDateUsd")]
        public double MonthToDateUsd { get; set; }

        [JsonProperty("todayUsd")]
        public double TodayUsd { get; set; }

        [JsonProperty("budgetUsd", NullValueHandling = NullValueHandling.Ignore)]
        public double? BudgetUsd { get; set; }

        /// <summary>monthToDate / budget, 0–100+.</summary>
        [JsonProperty("percentOfBudget", NullValueHandling = NullValueHandling.Ignore)]
        public double? PercentOfBudget { get; set; }

        [JsonProperty("tokensToday", NullValueHandling = NullValueHandling.Ignore)]
        public TokenTotals? TokensToday { get; set; }

        [JsonProperty("fetchedAt")]
        public string FetchedAt { get; set; } = "";

        /// <summary>Shallow copy (TokenTotals is shared, it is never mutated after fetch).</summary>
        public ApiCost Clone() => (ApiCost)MemberwiseClone();
    }

    public sealed class Snapshot
    {
        public SubscriptionUsage? Subscription { get; set; }
        public string? SubscriptionError { get; set; }
        public ApiRateLimits? RateLimits { get; set; }
        public string? RateLimitsError { get; set; }
        public ApiCost? Cost { get; set; }
        public string? CostError { get; set; }
        public string UpdatedAt { get; set; } = Iso.EpochZero;
    }

    public sealed class Thresholds
    {
        public Thresholds(double warn, double crit)
        {
            Warn = warn;
            Crit = crit;
        }

        public double Warn { get; }
        public double Crit { get; }

        public static readonly Thresholds Default = new Thresholds(75, 90);
    }
}
