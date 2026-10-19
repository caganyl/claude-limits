using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeLimits.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClaudeLimits.Core.Tests
{
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-limits-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (Exception) { /* ignore */ }
        }
    }

    public class SharedCacheTests
    {
        [Fact]
        public void KeyIdMatchesTypeScript()
        {
            // python3 -c "import hashlib;print(hashlib.sha256(b'test-key').hexdigest()[:12])"
            Assert.Equal("62af8704764f", SharedCache.KeyId("test-key"));
            Assert.Equal("62af8704764f|claude-haiku-5-5", SharedCache.KeyId("test-key", "claude-haiku-5-5"));
            Assert.Equal("69a5265506c9", SharedCache.KeyId("admin-key"));
            // UTF-8 encoding of non-ASCII secrets (Node's createHash().update(string) uses UTF-8).
            Assert.Equal("73fc57f137a4", SharedCache.KeyId("sk-ant-api03-ÇĞ"));
        }

        [Fact]
        public void MissingOrCorruptFileYieldsEmptyDocument()
        {
            using (var dir = new TempDir())
            {
                var cache = new SharedCache(dir.Path);
                Assert.Equal(1, cache.Read()["version"]!.Value<int>());
                File.WriteAllText(cache.FilePath, "{ not json");
                Assert.Null(cache.Get(CacheSections.Subscription));
                File.WriteAllText(cache.FilePath, "{\"version\":2,\"subscription\":{\"fetchedAt\":\"x\"}}");
                Assert.Null(cache.Get(CacheSections.Subscription));
            }
        }

        [Fact]
        public void RoundTripPreservesUnknownSectionsAndFields()
        {
            using (var dir = new TempDir())
            {
                var cache = new SharedCache(dir.Path);
                File.WriteAllText(cache.FilePath, @"{
                  ""version"": 1,
                  ""futureSection"": { ""hello"": ""world"", ""n"": [1, 2, 3] },
                  ""cost"": { ""fetchedAt"": ""2026-10-19T11:00:00.000Z"", ""keyId"": ""abc"",
                              ""data"": { ""monthToDateUsd"": 12.5, ""todayUsd"": 0.8, ""fetchedAt"": ""2026-10-19T11:00:00.000Z"", ""someNewField"": true } },
                  ""topLevelExtra"": 42
                }");

                var usage = new SubscriptionUsage
                {
                    Windows = new List<LimitWindow>
                    {
                        new LimitWindow { Id = "session", Label = "Oturum (5 saat)", ShortLabel = "5s", Percent = 42, ResetsAt = "2026-10-19T14:00:00+00:00", Active = true },
                        new LimitWindow { Id = "weekly_all", Label = "Haftalık (tüm modeller)", ShortLabel = "7g", Percent = 18.5 },
                    },
                    Extra = new ExtraUsage { Enabled = true, Percent = 5, MonthlyLimitUsd = 50 },
                    Plan = "max",
                    Source = "claude-code",
                    FetchedAt = "2026-10-19T11:20:00.000Z",
                };
                cache.Put(CacheSections.Subscription, new CacheEntry
                {
                    FetchedAt = "2026-10-19T11:20:00.000Z",
                    Data = JsonUtil.ToToken(usage),
                    Error = "boom",
                    ErrorKind = "network",
                });

                var raw = File.ReadAllText(cache.FilePath);
                var doc = JObject.Parse(raw);
                Assert.Equal("world", doc["futureSection"]!["hello"]!.Value<string>());
                Assert.Equal(42, doc["topLevelExtra"]!.Value<int>());
                Assert.True(doc["cost"]!["data"]!["someNewField"]!.Value<bool>());
                Assert.Equal("abc", doc["cost"]!["keyId"]!.Value<string>());

                var sub = doc["subscription"]!;
                Assert.Null(sub["keyId"]); // no manual token → field absent, not null
                var w0 = sub["data"]!["windows"]![0]!;
                Assert.Equal(new[] { "id", "label", "shortLabel", "percent", "resetsAt", "active" }, ((JObject)w0).Properties().Select(p => p.Name).ToArray());
                Assert.Equal(JTokenType.Integer, w0["percent"]!.Type); // 42, not 42.0
                Assert.True(w0["active"]!.Value<bool>());
                Assert.Null(sub["data"]!["windows"]![1]!["active"]);
                Assert.Null(sub["data"]!["windows"]![1]!["resetsAt"]);
                Assert.Equal(50, sub["data"]!["extra"]!["monthlyLimitUsd"]!.Value<int>());
                Assert.DoesNotContain("\r\n", raw);

                var back = cache.Get(CacheSections.Subscription)!;
                Assert.Equal("boom", back.Error);
                Assert.Equal("network", back.ErrorKind);
                var parsed = back.DataAs<SubscriptionUsage>()!;
                Assert.Equal(2, parsed.Windows.Count);
                Assert.Equal(18.5, parsed.Windows[1].Percent);
                Assert.Equal("2026-10-19T14:00:00+00:00", parsed.Windows[0].ResetsAt); // strings are not re-formatted as dates
                Assert.Equal("max", parsed.Plan);

                var cost = cache.Get(CacheSections.Cost)!.DataAs<ApiCost>()!;
                Assert.Equal(12.5, cost.MonthToDateUsd);
                Assert.Null(cost.TokensToday);
            }
        }
    }

    public class LimitsServiceTests
    {
        private sealed class FakeFetcher : ILimitsFetcher
        {
            public int SubscriptionCalls;
            public Exception? SubscriptionError;
            public double Percent = 42;

            public Task<SubscriptionUsage> FetchSubscriptionAsync(ServiceConfig cfg, CancellationToken ct)
            {
                Interlocked.Increment(ref SubscriptionCalls);
                if (SubscriptionError != null) throw SubscriptionError;
                return Task.FromResult(new SubscriptionUsage
                {
                    Windows = new List<LimitWindow> { new LimitWindow { Id = "session", Label = "Oturum (5 saat)", ShortLabel = "5s", Percent = Percent } },
                    FetchedAt = "2026-10-19T12:00:00.000Z",
                });
            }

            public Task<ApiRateLimits> FetchRateLimitsAsync(ServiceConfig cfg, CancellationToken ct) =>
                Task.FromResult(new ApiRateLimits { Model = cfg.ProbeModel, FetchedAt = "2026-10-19T12:00:00.000Z" });

            public Task<ApiCost> FetchCostAsync(ServiceConfig cfg, CancellationToken ct) =>
                Task.FromResult(new ApiCost { MonthToDateUsd = 25, TodayUsd = 1, FetchedAt = "2026-10-19T12:00:00.000Z" });
        }

        [Fact]
        public async Task HonoursIntervalsForcedFloorBackoffAndKeepsLastGoodData()
        {
            using (var dir = new TempDir())
            {
                var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
                var fake = new FakeFetcher();
                var cfg = new ServiceConfig { SubscriptionIntervalSec = 10 }; // below the 60 s floor
                using (var svc = new LimitsService(() => cfg, new SharedCache(dir.Path), fake, () => now))
                {
                    var s1 = await svc.RefreshAsync();
                    Assert.Equal(1, fake.SubscriptionCalls);
                    Assert.Equal(42, s1.Subscription!.Windows[0].Percent);
                    Assert.Null(s1.RateLimits);
                    Assert.Null(s1.Cost);

                    now = now.AddSeconds(30); // < 60 s floor
                    await svc.RefreshAsync();
                    Assert.Equal(1, fake.SubscriptionCalls);

                    now = now.AddSeconds(-20); // forced but only 10 s old → still cached
                    await svc.RefreshAsync(force: true);
                    Assert.Equal(1, fake.SubscriptionCalls);

                    now = now.AddSeconds(10); // 20 s old, forced → fetch
                    fake.SubscriptionError = new LimitsError(LimitsErrorKind.RateLimited, "Çok sık istek; biraz sonra tekrar denenecek", 120);
                    var s2 = await svc.RefreshAsync(force: true);
                    Assert.Equal(2, fake.SubscriptionCalls);
                    Assert.Equal("Çok sık istek; biraz sonra tekrar denenecek", s2.SubscriptionError);
                    Assert.Equal(42, s2.Subscription!.Windows[0].Percent); // last good data kept

                    var entry = new SharedCache(dir.Path).Get(CacheSections.Subscription)!;
                    Assert.Equal("rate_limited", entry.ErrorKind);
                    Assert.Equal(Iso.Format(now.AddSeconds(120)), entry.BackoffUntil);

                    now = now.AddSeconds(100); // inside back-off, even forced
                    await svc.RefreshAsync(force: true);
                    Assert.Equal(2, fake.SubscriptionCalls);

                    now = now.AddSeconds(30); // back-off over
                    fake.SubscriptionError = null;
                    fake.Percent = 55;
                    var s3 = await svc.RefreshAsync(force: true);
                    Assert.Equal(3, fake.SubscriptionCalls);
                    Assert.Null(s3.SubscriptionError);
                    Assert.Equal(55, s3.Subscription!.Windows[0].Percent);
                }
            }
        }

        [Fact]
        public async Task BudgetIsReappliedLocallyAndKeysAreHashed()
        {
            using (var dir = new TempDir())
            {
                var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
                var cfg = new ServiceConfig { SubscriptionEnabled = false, AdminKey = "admin-key", ApiKey = "test-key", MonthlyBudgetUsd = 100 };
                using (var svc = new LimitsService(() => cfg, new SharedCache(dir.Path), new FakeFetcher(), () => now))
                {
                    var s = await svc.RefreshAsync();
                    Assert.Null(s.Subscription);
                    Assert.Equal(25.0, s.Cost!.PercentOfBudget!.Value);
                    Assert.Equal(100.0, s.Cost.BudgetUsd!.Value);

                    cfg.MonthlyBudgetUsd = 0;
                    s = await svc.RefreshAsync();
                    Assert.Null(s.Cost!.PercentOfBudget);
                    Assert.Null(s.Cost.BudgetUsd);

                    var raw = File.ReadAllText(Path.Combine(dir.Path, "cache.json"));
                    Assert.DoesNotContain("admin-key", raw);
                    Assert.DoesNotContain("test-key", raw);
                    var doc = JObject.Parse(raw);
                    Assert.Equal("69a5265506c9", doc["cost"]!["keyId"]!.Value<string>());
                    Assert.Equal("62af8704764f|claude-haiku-5-5", doc["rateLimits"]!["keyId"]!.Value<string>());
                }
            }
        }

        [Fact]
        public void IntervalFloors()
        {
            var cfg = new ServiceConfig { SubscriptionIntervalSec = 1, RateLimitIntervalSec = 1, CostIntervalSec = 1 };
            Assert.Equal(60_000, LimitsService.IntervalMs(CacheSections.Subscription, cfg));
            Assert.Equal(120_000, LimitsService.IntervalMs(CacheSections.RateLimits, cfg));
            Assert.Equal(120_000, LimitsService.IntervalMs(CacheSections.Cost, cfg));
            cfg.SubscriptionIntervalSec = double.NaN;
            Assert.Equal(180_000, LimitsService.IntervalMs(CacheSections.Subscription, cfg));
        }
    }

    public class CredentialReaderTests
    {
        [Fact]
        public void ManualTokenWinsThenEnvThenFile()
        {
            using (var dir = new TempDir())
            {
                var env = new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = " env-token " };
                Func<string, string?> getEnv = k => env.TryGetValue(k, out var v) ? v : null;
                Assert.Equal("manual", CredentialReader.Read(new CredentialOptions { ManualToken = " manual ", GetEnv = getEnv, ConfigDir = dir.Path }).AccessToken);
                Assert.Equal("env-token", CredentialReader.Read(new CredentialOptions { GetEnv = getEnv, ConfigDir = dir.Path }).AccessToken);

                env.Clear();
                var ex = Assert.Throws<LimitsError>(() => CredentialReader.Read(new CredentialOptions { GetEnv = getEnv, ConfigDir = dir.Path }));
                Assert.Equal(LimitsErrorKind.NoCredentials, ex.Kind);

                var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
                var future = now.AddHours(1).ToUnixTimeMilliseconds();
                File.WriteAllText(Path.Combine(dir.Path, ".credentials.json"),
                    "{\"claudeAiOauth\":{\"accessToken\":\"file-token\",\"refreshToken\":\"r\",\"expiresAt\":" + future + ",\"subscriptionType\":\"max\"}}");
                var c = CredentialReader.Read(new CredentialOptions { GetEnv = getEnv, ConfigDir = dir.Path, Now = now });
                Assert.Equal("file-token", c.AccessToken);
                Assert.Equal("max", c.Plan);
                Assert.Equal((double)future, c.ExpiresAt!.Value);
            }
        }

        [Fact]
        public void ExpiredTokenGivesSpecificError()
        {
            using (var dir = new TempDir())
            {
                var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
                File.WriteAllText(Path.Combine(dir.Path, ".credentials.json"),
                    "{\"claudeAiOauth\":{\"accessToken\":\"t\",\"expiresAt\":" + now.AddMinutes(-1).ToUnixTimeMilliseconds() + "}}");
                var ex = Assert.Throws<LimitsError>(() => CredentialReader.Read(new CredentialOptions { GetEnv = _ => null, ConfigDir = dir.Path, Now = now }));
                Assert.Equal(LimitsErrorKind.Expired, ex.Kind);
                Assert.Equal("expired", ex.KindString);
            }
        }
    }
}
