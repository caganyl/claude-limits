using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeLimits.Core;
using Xunit;

namespace ClaudeLimits.Core.Tests
{
    public class UsageParserTests
    {
        [Fact]
        public void ParsesLimitsArray()
        {
            const string json = @"{
              ""limits"": [
                { ""kind"": ""session"", ""percent"": 42.4, ""resets_at"": ""2026-10-19T14:00:00+00:00"", ""is_active"": true },
                { ""kind"": ""weekly_all"", ""utilization"": 18, ""resets_at"": ""2026-10-23T08:00:00+00:00"" },
                { ""kind"": ""weekly_scoped"", ""percent"": 31, ""scope"": { ""model"": { ""id"": ""claude-fable"", ""display_name"": ""Fable"" } } },
                { ""kind"": ""weekly_scoped"", ""percent"": ""7"", ""scope"": { ""model"": { ""id"": ""claude-opus"" } } },
                { ""kind"": ""session"" },
                ""garbage""
              ]
            }";
            var r = UsageParser.Parse(json);
            Assert.Equal(new[] { "session", "weekly_all", "weekly_scoped:Fable", "weekly_scoped:claude-opus" }, r.Windows.Select(w => w.Id).ToArray());

            var session = r.Windows[0];
            Assert.Equal("Oturum (5 saat)", session.Label);
            Assert.Equal("5s", session.ShortLabel);
            Assert.Equal(42.4, session.Percent, 6);
            Assert.True(session.IsActive);
            Assert.Equal("2026-10-19T14:00:00+00:00", session.ResetsAt);

            var weekly = r.Windows[1];
            Assert.Equal("Haftalık (tüm modeller)", weekly.Label);
            Assert.Equal("7g", weekly.ShortLabel);
            Assert.Equal(18, weekly.Percent);
            Assert.Null(weekly.Active);

            var fable = r.Windows[2];
            Assert.Equal("Haftalık · Fable", fable.Label);
            Assert.Equal("7g Fable", fable.ShortLabel);
            Assert.Equal(31, fable.Percent);
            Assert.Null(fable.ResetsAt);

            Assert.Equal(7, r.Windows[3].Percent);
            Assert.Null(r.Extra);
        }

        [Fact]
        public void UnknownKindInLimitsArrayIsPrettified()
        {
            var r = UsageParser.Parse(@"{ ""limits"": [ { ""kind"": ""monthly_cap"", ""percent"": 150, ""scope"": { ""model"": { ""display_name"": ""Opus"" } } } ] }");
            var w = Assert.Single(r.Windows);
            Assert.Equal("monthly_cap:Opus", w.Id);
            Assert.Equal("Monthly Cap · Opus", w.Label);
            Assert.Equal("monthly_cap", w.ShortLabel);
            Assert.Equal(100, w.Percent); // clamped
        }

        [Fact]
        public void FallsBackToFlatKeys()
        {
            const string json = @"{
              ""five_hour"": { ""utilization"": 12.5, ""resets_at"": ""2026-10-19T14:00:00Z"" },
              ""seven_day"": { ""utilization"": 40, ""resets_at"": null },
              ""seven_day_opus"": null,
              ""seven_day_sonnet"": { ""utilization"": null },
              ""brand_new_bucket"": { ""utilization"": 3 },
              ""extra_usage"": { ""utilization"": 99 },
              ""something"": 5
            }";
            var r = UsageParser.Parse(json);
            Assert.Equal(new[] { "session", "weekly_all", "brand_new_bucket" }, r.Windows.Select(w => w.Id).ToArray());
            Assert.Equal("2026-10-19T14:00:00Z", r.Windows[0].ResetsAt);
            Assert.Null(r.Windows[1].ResetsAt);
            Assert.Equal("Brand New Bucket", r.Windows[2].Label);
            Assert.Equal("brand_new_bucket", r.Windows[2].ShortLabel);
            Assert.Equal(3, r.Windows[2].Percent);
        }

        [Fact]
        public void LimitsArrayWinsOverFlatKeysAndFlatFillsGaps()
        {
            const string json = @"{
              ""limits"": [ { ""kind"": ""session"", ""percent"": 50, ""is_active"": true } ],
              ""five_hour"": { ""utilization"": 10 },
              ""seven_day"": { ""utilization"": 20 },
              ""seven_day_opus"": { ""utilization"": 30 }
            }";
            var r = UsageParser.Parse(json);
            Assert.Equal(new[] { "session", "weekly_all", "weekly_scoped:Opus" }, r.Windows.Select(w => w.Id).ToArray());
            Assert.Equal(50, r.Windows[0].Percent);
            Assert.True(r.Windows[0].IsActive);
            Assert.Equal(20, r.Windows[1].Percent);
            Assert.Equal("Haftalık · Opus", r.Windows[2].Label);
        }

        [Fact]
        public void ParsesExtraUsageInCents()
        {
            var r = UsageParser.Parse(@"{ ""extra_usage"": { ""is_enabled"": true, ""utilization"": 5, ""monthly_limit"": 100000 } }");
            Assert.NotNull(r.Extra);
            Assert.True(r.Extra!.Enabled);
            Assert.Equal(5.0, r.Extra.Percent!.Value);
            Assert.Equal(1000.0, r.Extra.MonthlyLimitUsd!.Value);

            var off = UsageParser.Parse(@"{ ""extra_usage"": { ""is_enabled"": false, ""monthly_limit"": 0 } }");
            Assert.False(off.Extra!.Enabled);
            Assert.Null(off.Extra.Percent);
            Assert.Null(off.Extra.MonthlyLimitUsd);
        }

        [Fact]
        public void RejectsNonObjectPayload()
        {
            var ex = Assert.Throws<FormatException>(() => UsageParser.Parse("[1,2]"));
            Assert.Equal("Beklenmeyen kullanım yanıtı", ex.Message);
        }
    }

    public class RateLimitParserTests
    {
        [Fact]
        public void ParsesHeaders()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic-ratelimit-requests-limit"] = "50",
                ["anthropic-ratelimit-requests-remaining"] = "49",
                ["anthropic-ratelimit-requests-reset"] = "2026-10-19T12:00:30Z",
                ["Anthropic-RateLimit-Input-Tokens-Limit"] = "40000",
                ["anthropic-ratelimit-input-tokens-remaining"] = "10000",
                ["anthropic-ratelimit-output-tokens-limit"] = "0",
                ["anthropic-ratelimit-output-tokens-remaining"] = "0",
                ["anthropic-ratelimit-tokens-limit"] = "abc",
                ["anthropic-ratelimit-tokens-remaining"] = "1",
            };
            var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
            var r = RateLimitParser.Parse(h => headers.TryGetValue(h, out var v) ? v : null, "claude-haiku-5-5", false, now);

            Assert.Equal("claude-haiku-5-5", r.Model);
            Assert.False(r.Limited);
            Assert.Equal("2026-10-19T12:00:00.000Z", r.FetchedAt);
            Assert.Equal(2, r.Buckets.Count);

            var req = r.Buckets[0];
            Assert.Equal("requests", req.Id);
            Assert.Equal("İstek / dk", req.Label);
            Assert.Equal(50, req.Limit);
            Assert.Equal(49, req.Remaining);
            Assert.Equal(2, req.PercentUsed, 6);
            Assert.Equal("2026-10-19T12:00:30Z", req.ResetsAt);

            var input = r.Buckets[1];
            Assert.Equal("input_tokens", input.Id);
            Assert.Equal("Girdi token / dk", input.Label);
            Assert.Equal(75, input.PercentUsed, 6);
            Assert.Null(input.ResetsAt);
        }

        [Fact]
        public void LimitedWithoutHeaders()
        {
            var r = RateLimitParser.Parse(_ => null, "m", true);
            Assert.True(r.Limited);
            Assert.Empty(r.Buckets);
        }
    }

    public class CostParserTests
    {
        [Fact]
        public void SumsCentStringsPerDay()
        {
            const string json = @"{
              ""data"": [
                { ""starting_at"": ""2026-10-18T00:00:00Z"", ""results"": [ { ""amount"": ""1234.5"", ""currency"": ""USD"" }, { ""amount"": ""100"", ""currency"": ""EUR"" } ] },
                { ""starting_at"": ""2026-10-19T00:00:00Z"", ""results"": [ { ""amount"": ""250"" }, { ""amount"": 50, ""currency"": ""USD"" } ] },
                { ""starting_at"": ""2026-10-19T00:00:00Z"", ""results"": [ { ""amount"": ""100"" } ] }
              ],
              ""has_more"": true,
              ""next_page"": ""page_2""
            }";
            var p = CostParser.ParseCostReportPage(json);
            Assert.Equal(2, p.PerDay.Count);
            Assert.Equal("2026-10-18T00:00:00Z", p.PerDay[0].Key);
            Assert.Equal(12.345, p.PerDay[0].Value, 9);
            Assert.Equal(4.0, p.PerDay[1].Value, 9);
            Assert.True(p.HasMore);
            Assert.Equal("page_2", p.NextPage);
        }

        [Fact]
        public void TodayBucketMatchesStartOfUtcDay()
        {
            var p = CostParser.ParseCostReportPage(@"{ ""data"": [
                { ""starting_at"": ""2026-10-01T00:00:00Z"", ""results"": [ { ""amount"": ""500"" } ] },
                { ""starting_at"": ""2026-10-19T00:00:00Z"", ""results"": [ { ""amount"": ""80"" } ] } ], ""has_more"": false }");
            var today = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            double month = 0, todayUsd = 0;
            foreach (var kv in p.PerDay)
            {
                month += kv.Value;
                if (Iso.ParseMs(kv.Key) == today) todayUsd += kv.Value;
            }
            Assert.Equal(5.8, month, 9);
            Assert.Equal(0.8, todayUsd, 9);
            Assert.False(p.HasMore);
            Assert.Null(p.NextPage);
        }

        [Fact]
        public void SumsUsageTokens()
        {
            const string json = @"{
              ""data"": [ { ""results"": [
                { ""uncached_input_tokens"": 100, ""cache_read_input_tokens"": 20, ""output_tokens"": 7,
                  ""cache_creation"": { ""ephemeral_5m_input_tokens"": 3, ""ephemeral_1h_input_tokens"": 4 } },
                { ""uncached_input_tokens"": ""5"", ""cache_creation_input_tokens"": 10, ""output_tokens"": 1 } ] },
                { ""results"": null } ],
              ""has_more"": false
            }";
            var p = CostParser.ParseUsageReportPage(json);
            Assert.Equal(105, p.Totals.UncachedInput);
            Assert.Equal(20, p.Totals.CacheRead);
            Assert.Equal(17, p.Totals.CacheWrite);
            Assert.Equal(8, p.Totals.Output);
        }
    }
}
