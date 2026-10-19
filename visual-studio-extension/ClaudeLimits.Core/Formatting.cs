// Mirrors core/src/format.ts (plus the small helpers from render.ts used by the panel).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudeLimits.Core
{
    public static class Formatting
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>tr-TR short weekday names (same as Intl "short"), Sunday first; hardcoded so ICU-less hosts still match.</summary>
        private static readonly string[] TrShortDays = { "Paz", "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt" };

        public static Level LevelOf(double? percent, Thresholds? t = null)
        {
            t = t ?? Thresholds.Default;
            if (percent == null || !Js.IsFinite(percent.Value)) return Level.Unknown;
            if (percent.Value >= t.Crit) return Level.Crit;
            if (percent.Value >= t.Warn) return Level.Warn;
            return Level.Ok;
        }

        private static int Rank(Level l)
        {
            switch (l)
            {
                case Level.Ok: return 0;
                case Level.Warn: return 1;
                case Level.Crit: return 2;
                default: return -1;
            }
        }

        public static Level WorstLevel(IEnumerable<Level> levels)
        {
            var acc = Level.Unknown;
            foreach (var l in levels) if (Rank(l) > Rank(acc)) acc = l;
            return acc;
        }

        public static string LevelString(Level l)
        {
            switch (l)
            {
                case Level.Ok: return "ok";
                case Level.Warn: return "warn";
                case Level.Crit: return "crit";
                default: return "unknown";
            }
        }

        public static string FormatPercent(double? p)
        {
            if (p == null || !Js.IsFinite(p.Value)) return "–";
            var r = Js.Round(p.Value);
            if (r == 0) r = 0; // normalise -0
            return r.ToString("0", Inv) + "%";
        }

        /// <summary>JavaScript-style Math.round as an integer string: 42.5 → "43". Used for "%42 kullanıldı".</summary>
        public static string RoundedNumber(double v)
        {
            if (!Js.IsFinite(v)) return "–";
            var r = Js.Round(v);
            if (r == 0) r = 0; // normalise -0
            return r.ToString("0", Inv);
        }

        /// <summary>"2 sa 15 dk", "3 gün 4 sa", "45 dk", "30 sn", "şimdi"; "" when unknown.</summary>
        public static string FormatResetIn(string? resetsAt, DateTimeOffset? now = null)
        {
            if (string.IsNullOrEmpty(resetsAt)) return "";
            var t = Iso.ParseMs(resetsAt);
            if (t == null) return "";
            var nowMs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            var secs = (long)Js.Round((t.Value - nowMs) / 1000.0);
            if (secs <= 0) return "şimdi";
            if (secs < 60) return secs.ToString(Inv) + " sn";
            var mins = (long)Js.Round(secs / 60.0);
            var days = mins / 1440;
            mins -= days * 1440;
            var hours = mins / 60;
            mins -= hours * 60;
            if (days > 0) return hours > 0 ? $"{days} gün {hours} sa" : $"{days} gün";
            if (hours > 0) return mins > 0 ? $"{hours} sa {mins} dk" : $"{hours} sa";
            return $"{mins} dk";
        }

        /// <summary>Local wall-clock reset time: "Pzt 18:00" or "18:00" when today.</summary>
        public static string FormatResetAt(string? resetsAt, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
        {
            if (string.IsNullOrEmpty(resetsAt)) return "";
            if (!Iso.TryParse(resetsAt, out var d)) return "";
            zone = zone ?? TimeZoneInfo.Local;
            var local = TimeZoneInfo.ConvertTime(d, zone);
            var localNow = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, zone);
            var time = local.ToString("HH:mm", Inv);
            if (local.Date == localNow.Date) return time;
            return TrShortDays[(int)local.DayOfWeek] + " " + time;
        }

        public static string FormatUsd(double? v)
        {
            if (v == null || !Js.IsFinite(v.Value)) return "–";
            return "$" + v.Value.ToString("F2", Inv);
        }

        public static string FormatCount(double n)
        {
            if (n >= 1_000_000) return (n / 1_000_000).ToString(n >= 10_000_000 ? "F0" : "F1", Inv) + "M";
            if (n >= 1_000) return (n / 1_000).ToString(n >= 10_000 ? "F0" : "F1", Inv) + "K";
            return Js.Round(n).ToString("0", Inv);
        }

        /// <summary>render.ts sinceText(): "az önce", "5 dk önce", "2 sa önce".</summary>
        public static string FormatSince(string? iso, DateTimeOffset? now = null)
        {
            var t = Iso.ParseMs(iso);
            if (t == null) return "";
            var nowMs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            var secs = Math.Max(0, Js.Round((nowMs - t.Value) / 1000.0));
            if (secs < 60) return "az önce";
            if (secs < 3600) return Js.Round(secs / 60).ToString("0", Inv) + " dk önce";
            return Js.Round(secs / 3600).ToString("0", Inv) + " sa önce";
        }

        /// <summary>render.ts resetLine(): "2 sa 15 dk sonra sıfırlanır · Pzt 18:00", "Sıfırlanıyor" or "".</summary>
        public static string ResetLine(string? resetsAt, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
        {
            if (string.IsNullOrEmpty(resetsAt)) return "";
            var inText = FormatResetIn(resetsAt, now);
            if (inText.Length == 0) return "";
            if (inText == "şimdi") return "Sıfırlanıyor";
            var at = FormatResetAt(resetsAt, now, zone);
            return inText + " sonra sıfırlanır" + (at.Length > 0 ? " · " + at : "");
        }

        /// <summary>Windows to show in a compact status line for the given mode.</summary>
        public static List<LimitWindow> PickCompactWindows(IList<LimitWindow> windows, DisplayMode mode)
        {
            var session = windows.FirstOrDefault(w => w.Id == "session");
            var weekly = windows.FirstOrDefault(w => w.Id == "weekly_all");
            var scopedMax = windows
                .Where(w => w.Id.StartsWith("weekly_scoped:", StringComparison.Ordinal))
                .OrderByDescending(w => w.Percent)
                .FirstOrDefault();
            // A per-model weekly cap that is binding or higher than the all-model one is the more useful number.
            var weeklyShown = scopedMax != null && (scopedMax.IsActive || weekly == null || scopedMax.Percent > weekly.Percent)
                ? scopedMax
                : weekly;
            switch (mode)
            {
                case DisplayMode.Session:
                    return session != null ? new List<LimitWindow> { session } : windows.Take(1).ToList();
                case DisplayMode.Weekly:
                    return weeklyShown != null ? new List<LimitWindow> { weeklyShown } : windows.Take(1).ToList();
                case DisplayMode.Highest:
                    {
                        var top = windows.OrderByDescending(w => w.Percent).FirstOrDefault();
                        return top != null ? new List<LimitWindow> { top } : new List<LimitWindow>();
                    }
                default:
                    {
                        var list = new List<LimitWindow>();
                        if (session != null) list.Add(session);
                        if (weeklyShown != null) list.Add(weeklyShown);
                        return list;
                    }
            }
        }

        /// <summary>Compact one-liner such as "5s 42% · 7g 18%".</summary>
        public static string CompactText(Snapshot snapshot, DisplayMode mode = DisplayMode.Both)
        {
            var parts = new List<string>();
            if (snapshot.Subscription != null)
            {
                foreach (var w in PickCompactWindows(snapshot.Subscription.Windows, mode))
                    parts.Add(w.ShortLabel + " " + FormatPercent(w.Percent));
            }
            if (parts.Count == 0 && snapshot.RateLimits != null)
            {
                var worst = snapshot.RateLimits.Buckets.OrderByDescending(b => b.PercentUsed).FirstOrDefault();
                if (worst != null) parts.Add("API " + FormatPercent(worst.PercentUsed));
            }
            if (parts.Count == 0 && snapshot.Cost != null) parts.Add("API " + FormatUsd(snapshot.Cost.MonthToDateUsd));
            return string.Join(" · ", parts);
        }

        /// <summary>Highest-severity level across everything in the snapshot.</summary>
        public static Level SnapshotLevel(Snapshot snapshot, Thresholds? t = null)
        {
            var levels = new List<Level>();
            if (snapshot.Subscription != null)
                foreach (var w in snapshot.Subscription.Windows) levels.Add(LevelOf(w.Percent, t));
            if (snapshot.RateLimits != null)
            {
                foreach (var b in snapshot.RateLimits.Buckets) levels.Add(LevelOf(b.PercentUsed, t));
                if (snapshot.RateLimits.Limited) levels.Add(Level.Crit);
            }
            if (snapshot.Cost?.PercentOfBudget != null) levels.Add(LevelOf(snapshot.Cost.PercentOfBudget, t));
            return WorstLevel(levels);
        }

        /// <summary>The most relevant subscription percent, else API.</summary>
        public static double? HeadlinePercent(Snapshot snapshot, DisplayMode mode = DisplayMode.Session)
        {
            var sub = snapshot.Subscription;
            if (sub != null && sub.Windows.Count > 0)
            {
                var picked = PickCompactWindows(sub.Windows, mode == DisplayMode.Both ? DisplayMode.Highest : mode);
                if (picked.Count > 0) return picked[0].Percent;
            }
            if (snapshot.RateLimits != null && snapshot.RateLimits.Buckets.Count > 0)
                return snapshot.RateLimits.Buckets.Max(b => b.PercentUsed);
            return snapshot.Cost?.PercentOfBudget;
        }

        public static string PlanName(string? plan)
        {
            if (string.IsNullOrEmpty(plan)) return "";
            switch (plan!.ToLowerInvariant())
            {
                case "pro": return "Pro";
                case "max": return "Max";
                case "team": return "Team";
                case "enterprise": return "Enterprise";
                case "free": return "Free";
                default: return plan;
            }
        }
    }
}
