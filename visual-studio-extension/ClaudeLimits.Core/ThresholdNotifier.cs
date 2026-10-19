// Mirrors core/src/notify.ts: a window notifies once per level (warn, then crit) per reset period.
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeLimits.Core
{
    public sealed class LimitAlert
    {
        public LimitAlert(string key, Level level, string message)
        {
            Key = key;
            Level = level;
            Message = message;
        }

        public string Key { get; }

        /// <summary>Warn or Crit.</summary>
        public Level Level { get; }

        public string Message { get; }
    }

    public sealed class ThresholdNotifier
    {
        public ThresholdNotifier(Dictionary<string, Level>? state = null)
        {
            State = state ?? new Dictionary<string, Level>(StringComparer.Ordinal);
        }

        /// <summary>key → highest level already announced.</summary>
        public Dictionary<string, Level> State { get; }

        private static int Rank(Level l) => l == Level.Crit ? 2 : l == Level.Warn ? 1 : 0;

        public List<LimitAlert> Check(Snapshot s, Thresholds t, DateTimeOffset? now = null)
        {
            var alerts = new List<LimitAlert>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Consider(string key, double percent, Func<string, string> text)
            {
                seen.Add(key);
                var lv = Formatting.LevelOf(percent, t);
                var prev = State.TryGetValue(key, out var p) ? p : Level.Ok;
                if (Rank(lv) > Rank(prev) && (lv == Level.Warn || lv == Level.Crit))
                    alerts.Add(new LimitAlert(key, lv, text(Formatting.FormatPercent(percent))));
                // Track drops too, so a later climb notifies again only after a reset.
                if (Rank(lv) > Rank(prev) || lv == Level.Ok) State[key] = lv;
            }

            if (s.Subscription != null)
            {
                foreach (var w in s.Subscription.Windows)
                {
                    if (w.IsUndocumented) continue;
                    var reset = Formatting.FormatResetIn(w.ResetsAt, now);
                    var label = w.Label;
                    Consider("sub:" + w.Id + ":" + (w.ResetsAt ?? ""), w.Percent, pct =>
                        "Claude " + label + " limiti " + pct +
                        (reset.Length > 0 && reset != "şimdi" ? " — " + reset + " sonra sıfırlanır" : "") + ".");
                }
            }
            if (s.RateLimits != null)
            {
                var model = s.RateLimits.Model;
                foreach (var b in s.RateLimits.Buckets)
                {
                    var label = b.Label;
                    Consider("rl:" + b.Id, b.PercentUsed, pct => "Claude API " + label + " kullanımı " + pct + " (" + model + ").");
                }
            }
            if (s.Cost?.PercentOfBudget != null)
            {
                var fetched = s.Cost.FetchedAt ?? "";
                var month = fetched.Length >= 7 ? fetched.Substring(0, 7) : fetched;
                Consider("cost:" + month, s.Cost.PercentOfBudget.Value, pct => "Claude API aylık bütçenin " + pct + " kadarı harcandı.");
            }
            // Forget windows that have rolled over (their key contains the old reset time).
            foreach (var k in State.Keys.ToList())
                if (!seen.Contains(k)) State.Remove(k);
            return alerts;
        }
    }
}
