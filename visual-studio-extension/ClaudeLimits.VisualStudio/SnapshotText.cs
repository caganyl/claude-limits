using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudeLimits.Core;
using DisplayMode = ClaudeLimits.Core.DisplayMode;
using Formatting = ClaudeLimits.Core.Formatting;
using Level = ClaudeLimits.Core.Level;
using Snapshot = ClaudeLimits.Core.Snapshot;
using Thresholds = ClaudeLimits.Core.Thresholds;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>Everything the UI needs to render one state.</summary>
    internal sealed class ViewState
    {
        public Snapshot Snapshot { get; set; } = new Snapshot();
        public Thresholds Thresholds { get; set; } = Thresholds.Default;
        public DisplayMode Mode { get; set; } = DisplayMode.Both;
        public bool SubscriptionEnabled { get; set; } = true;
        public bool HasApiKey { get; set; }
        public bool HasAdminKey { get; set; }
    }

    /// <summary>Plain-text renderings shared by the status bar indicator and its tooltip.</summary>
    internal static class SnapshotText
    {
        public static string StatusText(ViewState v)
        {
            var compact = Formatting.CompactText(v.Snapshot, v.Mode);
            if (compact.Length > 0) return "Claude " + compact;
            var s = v.Snapshot;
            if (s.SubscriptionError != null || s.RateLimitsError != null || s.CostError != null) return "Claude ⚠";
            return "Claude …";
        }

        public static string UpdatedAt(Snapshot s)
        {
            var ms = Iso.ParseMs(s.UpdatedAt);
            if (ms == null || ms.Value <= 0) return "–";
            return DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>"Sıfırlanma: 2 sa 15 dk (Pzt 18:00)", "Sıfırlanıyor" or "".</summary>
        public static string ResetText(string resetsAt, DateTimeOffset now)
        {
            var inText = Formatting.FormatResetIn(resetsAt, now);
            if (inText.Length == 0) return "";
            if (inText == "şimdi") return "Sıfırlanıyor";
            var at = Formatting.FormatResetAt(resetsAt, now);
            return "Sıfırlanma: " + inText + (at.Length > 0 ? " (" + at + ")" : "");
        }

        public static string Tooltip(ViewState v)
        {
            var s = v.Snapshot;
            var now = DateTimeOffset.UtcNow;
            var lines = new List<string> { "Claude Limits" };

            if (s.Subscription != null && s.Subscription.Windows.Count > 0)
            {
                lines.Add("");
                var chip = new List<string>();
                var plan = Formatting.PlanName(s.Subscription.Plan);
                if (plan.Length > 0) chip.Add(plan);
                chip.Add(s.Subscription.Source == "claude-web" ? "claude.ai" : "Claude Code");
                lines.Add("Abonelik (" + string.Join(" · ", chip) + ")");
                foreach (var w in s.Subscription.Windows.Where(x => !x.IsUndocumented))
                {
                    var reset = ResetText(w.ResetsAt ?? "", now);
                    lines.Add("  " + w.Label + ": " + Formatting.FormatPercent(w.Percent)
                              + (w.IsActive ? " (bağlayıcı)" : "")
                              + (reset.Length > 0 ? " — " + reset : ""));
                }
                var extra = s.Subscription.Extra;
                if (extra != null && extra.Enabled)
                {
                    lines.Add("  Ek kullanım (aylık): " + (extra.Percent.HasValue ? Formatting.FormatPercent(extra.Percent) : "açık")
                              + (extra.MonthlyLimitUsd.HasValue ? " · limit " + Formatting.FormatUsd(extra.MonthlyLimitUsd) : ""));
                }
            }
            if (s.SubscriptionError != null) lines.Add("⚠ " + s.SubscriptionError);

            if (s.RateLimits != null)
            {
                lines.Add("");
                lines.Add("API rate limit (" + s.RateLimits.Model + ")" + (s.RateLimits.Limited ? " — 429 – limit dolu" : ""));
                foreach (var b in s.RateLimits.Buckets)
                {
                    lines.Add("  " + b.Label + ": " + Formatting.FormatCount(b.Remaining) + " / " + Formatting.FormatCount(b.Limit) + " kaldı (%"
                              + Formatting.RoundedNumber(b.PercentUsed) + " kullanıldı)");
                }
            }
            if (s.RateLimitsError != null) lines.Add("⚠ " + s.RateLimitsError);

            if (s.Cost != null)
            {
                lines.Add("");
                var cost = "API harcama: bu ay " + Formatting.FormatUsd(s.Cost.MonthToDateUsd) + " · bugün " + Formatting.FormatUsd(s.Cost.TodayUsd);
                if (s.Cost.PercentOfBudget.HasValue) cost += " · bütçe " + Formatting.FormatPercent(s.Cost.PercentOfBudget);
                lines.Add(cost);
            }
            if (s.CostError != null) lines.Add("⚠ " + s.CostError);

            lines.Add("");
            lines.Add("Son güncelleme: " + UpdatedAt(s));
            lines.Add("Ayrıntılar için tıklayın.");
            return string.Join(Environment.NewLine, lines);
        }

        public static Level StatusLevel(ViewState v) => Formatting.SnapshotLevel(v.Snapshot, v.Thresholds);

        public static bool AnyData(Snapshot s) =>
            (s.Subscription != null && s.Subscription.Windows.Any()) || s.RateLimits != null || s.Cost != null;
    }
}
