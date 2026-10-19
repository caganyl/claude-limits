using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeLimits.Core;
using Xunit;

namespace ClaudeLimits.Core.Tests
{
    public class FormattingTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        private static string At(TimeSpan offset) => Iso.Format(Now + offset);

        [Fact]
        public void ResetCountdown()
        {
            Assert.Equal("2 sa 15 dk", Formatting.FormatResetIn(At(new TimeSpan(2, 15, 0)), Now));
            Assert.Equal("3 gün 4 sa", Formatting.FormatResetIn(At(new TimeSpan(3, 4, 0, 0)), Now));
            Assert.Equal("3 gün", Formatting.FormatResetIn(At(TimeSpan.FromDays(3)), Now));
            Assert.Equal("2 sa", Formatting.FormatResetIn(At(TimeSpan.FromHours(2)), Now));
            Assert.Equal("45 dk", Formatting.FormatResetIn(At(TimeSpan.FromMinutes(45)), Now));
            Assert.Equal("şimdi", Formatting.FormatResetIn(At(TimeSpan.Zero), Now));
            Assert.Equal("şimdi", Formatting.FormatResetIn(At(TimeSpan.FromMinutes(-5)), Now));
            Assert.Equal("", Formatting.FormatResetIn(null, Now));
            Assert.Equal("", Formatting.FormatResetIn("not a date", Now));
        }

        [Fact]
        public void ResetCountdownSubMinuteUsesSeconds()
        {
            Assert.Equal("30 sn", Formatting.FormatResetIn(At(TimeSpan.FromSeconds(30)), Now));
            Assert.Equal("59 sn", Formatting.FormatResetIn(At(TimeSpan.FromSeconds(59)), Now));
            Assert.Equal("1 dk", Formatting.FormatResetIn(At(TimeSpan.FromSeconds(60)), Now));
            Assert.Equal("1 dk", Formatting.FormatResetIn(At(TimeSpan.FromSeconds(89)), Now));
            Assert.Equal("2 dk", Formatting.FormatResetIn(At(TimeSpan.FromSeconds(90)), Now));
            Assert.Equal("şimdi", Formatting.FormatResetIn(At(TimeSpan.FromMilliseconds(400)), Now));
        }

        [Fact]
        public void ResetCountdownAcceptsOffsetTimestamps()
        {
            Assert.Equal("2 sa", Formatting.FormatResetIn("2026-10-19T14:00:00+00:00", Now));
            Assert.Equal("2 sa", Formatting.FormatResetIn("2026-10-19T17:00:00+03:00", Now));
        }

        [Fact]
        public void ResetAtShowsWeekdayWhenNotToday()
        {
            Assert.Equal("18:00", Formatting.FormatResetAt("2026-10-19T18:00:00Z", Now, TimeZoneInfo.Utc));
            // 2026-10-26 is a Monday.
            Assert.Equal("Pzt 18:00", Formatting.FormatResetAt("2026-10-26T18:00:00Z", Now, TimeZoneInfo.Utc));
            Assert.Equal("2 sa sonra sıfırlanır · 14:00", Formatting.ResetLine("2026-10-19T14:00:00Z", Now, TimeZoneInfo.Utc));
            Assert.Equal("Sıfırlanıyor", Formatting.ResetLine("2026-10-19T11:00:00Z", Now, TimeZoneInfo.Utc));
        }

        [Fact]
        public void LevelThresholds()
        {
            Assert.Equal(Level.Unknown, Formatting.LevelOf(null));
            Assert.Equal(Level.Unknown, Formatting.LevelOf(double.NaN));
            Assert.Equal(Level.Ok, Formatting.LevelOf(0));
            Assert.Equal(Level.Ok, Formatting.LevelOf(74.9));
            Assert.Equal(Level.Warn, Formatting.LevelOf(75));
            Assert.Equal(Level.Warn, Formatting.LevelOf(89.99));
            Assert.Equal(Level.Crit, Formatting.LevelOf(90));
            Assert.Equal(Level.Crit, Formatting.LevelOf(120));
            var t = new Thresholds(50, 60);
            Assert.Equal(Level.Warn, Formatting.LevelOf(55, t));
            Assert.Equal(Level.Crit, Formatting.LevelOf(60, t));
            Assert.Equal(Level.Crit, Formatting.WorstLevel(new[] { Level.Ok, Level.Crit, Level.Warn }));
            Assert.Equal(Level.Unknown, Formatting.WorstLevel(Array.Empty<Level>()));
        }

        [Fact]
        public void NumbersAndMoney()
        {
            Assert.Equal("42%", Formatting.FormatPercent(42.4));
            Assert.Equal("43%", Formatting.FormatPercent(42.5));
            Assert.Equal("–", Formatting.FormatPercent(null));
            Assert.Equal("$12.50", Formatting.FormatUsd(12.5));
            Assert.Equal("–", Formatting.FormatUsd(null));
            Assert.Equal("999", Formatting.FormatCount(999));
            Assert.Equal("1.5K", Formatting.FormatCount(1500));
            Assert.Equal("40K", Formatting.FormatCount(40000));
            Assert.Equal("2.5M", Formatting.FormatCount(2_500_000));
            Assert.Equal("12M", Formatting.FormatCount(12_000_000));
            Assert.Equal("az önce", Formatting.FormatSince(Iso.Format(Now.AddSeconds(-30)), Now));
            Assert.Equal("15 dk önce", Formatting.FormatSince(Iso.Format(Now.AddMinutes(-15)), Now));
            Assert.Equal("2 sa önce", Formatting.FormatSince(Iso.Format(Now.AddHours(-2)), Now));
        }

        private static List<LimitWindow> Windows() => new List<LimitWindow>
        {
            new LimitWindow { Id = "session", Label = "Oturum (5 saat)", ShortLabel = "5s", Percent = 42 },
            new LimitWindow { Id = "weekly_all", Label = "Haftalık (tüm modeller)", ShortLabel = "7g", Percent = 18 },
            new LimitWindow { Id = "weekly_scoped:Opus", Label = "Haftalık · Opus", ShortLabel = "7g Opus", Percent = 31 },
        };

        [Fact]
        public void CompactWindowsAndText()
        {
            var w = Windows();
            Assert.Equal(new[] { "session", "weekly_scoped:Opus" }, Formatting.PickCompactWindows(w, DisplayMode.Both).Select(x => x.Id).ToArray());
            Assert.Equal(new[] { "session" }, Formatting.PickCompactWindows(w, DisplayMode.Session).Select(x => x.Id).ToArray());
            Assert.Equal(new[] { "weekly_scoped:Opus" }, Formatting.PickCompactWindows(w, DisplayMode.Weekly).Select(x => x.Id).ToArray());
            Assert.Equal(new[] { "session" }, Formatting.PickCompactWindows(w, DisplayMode.Highest).Select(x => x.Id).ToArray());

            w[2].Percent = 10;
            Assert.Equal(new[] { "session", "weekly_all" }, Formatting.PickCompactWindows(w, DisplayMode.Both).Select(x => x.Id).ToArray());
            w[2].Active = true;
            Assert.Equal(new[] { "session", "weekly_scoped:Opus" }, Formatting.PickCompactWindows(w, DisplayMode.Both).Select(x => x.Id).ToArray());

            var s = new Snapshot { Subscription = new SubscriptionUsage { Windows = Windows() } };
            s.Subscription.Windows[2].Percent = 10;
            Assert.Equal("5s 42% · 7g 18%", Formatting.CompactText(s, DisplayMode.Both));

            var api = new Snapshot
            {
                RateLimits = new ApiRateLimits
                {
                    Buckets = new List<RateLimitBucket> { new RateLimitBucket { PercentUsed = 5 }, new RateLimitBucket { PercentUsed = 80 } },
                },
            };
            Assert.Equal("API 80%", Formatting.CompactText(api));
            Assert.Equal(Level.Warn, Formatting.SnapshotLevel(api));
            Assert.Equal("API $3.20", Formatting.CompactText(new Snapshot { Cost = new ApiCost { MonthToDateUsd = 3.2 } }));
            Assert.Equal("", Formatting.CompactText(new Snapshot()));
        }

        [Fact]
        public void NotifierFiresOncePerLevelPerResetPeriod()
        {
            var n = new ThresholdNotifier();
            var t = Thresholds.Default;
            var w = new LimitWindow { Id = "session", Label = "Oturum (5 saat)", ShortLabel = "5s", Percent = 80, ResetsAt = At(new TimeSpan(1, 10, 0)) };
            var s = new Snapshot { Subscription = new SubscriptionUsage { Windows = new List<LimitWindow> { w } } };

            var a1 = n.Check(s, t, Now);
            Assert.Single(a1);
            Assert.Equal(Level.Warn, a1[0].Level);
            Assert.Equal("Claude Oturum (5 saat) limiti 80% — 1 sa 10 dk sonra sıfırlanır.", a1[0].Message);
            Assert.Empty(n.Check(s, t, Now));

            w.Percent = 92;
            var a2 = n.Check(s, t, Now);
            Assert.Single(a2);
            Assert.Equal(Level.Crit, a2[0].Level);
            Assert.Empty(n.Check(s, t, Now));

            // New reset period → new key → notifies again.
            w.ResetsAt = At(TimeSpan.FromHours(6));
            Assert.Single(n.Check(s, t, Now));
        }
    }
}
