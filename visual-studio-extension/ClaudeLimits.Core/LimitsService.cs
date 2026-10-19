// Mirrors core/src/service.ts: polls every source on its own interval, shares results through the
// cache file, and hands a merged Snapshot to the UI.
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    public sealed class ServiceConfig
    {
        /// <summary>Show subscription (Pro/Max/Team) limits from the Claude Code login.</summary>
        public bool SubscriptionEnabled { get; set; } = true;

        /// <summary>Optional token pasted by the user instead of the Claude Code login.</summary>
        public string? ManualOAuthToken { get; set; }

        /// <summary>Standard API key (sk-ant-api…) for per-minute rate limits.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Model whose rate limits are probed.</summary>
        public string ProbeModel { get; set; } = DefaultProbeModel;

        /// <summary>Admin API key (sk-ant-admin…) for spend &amp; tokens.</summary>
        public string? AdminKey { get; set; }

        public double? MonthlyBudgetUsd { get; set; }
        public double SubscriptionIntervalSec { get; set; } = 180;
        public double RateLimitIntervalSec { get; set; } = 900;
        public double CostIntervalSec { get; set; } = 600;

        /// <summary>"auto" | "direct" | proxy URL — see AnthropicClient.ProxySetting.</summary>
        public string Proxy { get; set; } = "auto";

        public const string DefaultProbeModel = "claude-haiku-5-5";
        public const double DefaultSubscriptionIntervalSec = 180;
    }

    /// <summary>Fetches each section; replaceable in tests.</summary>
    public interface ILimitsFetcher
    {
        Task<SubscriptionUsage> FetchSubscriptionAsync(ServiceConfig cfg, CancellationToken ct);
        Task<ApiRateLimits> FetchRateLimitsAsync(ServiceConfig cfg, CancellationToken ct);
        Task<ApiCost> FetchCostAsync(ServiceConfig cfg, CancellationToken ct);
    }

    /// <summary>Default fetcher backed by <see cref="AnthropicClient"/> and <see cref="CredentialReader"/>.</summary>
    public sealed class AnthropicFetcher : ILimitsFetcher, IDisposable
    {
        private readonly AnthropicClient _client;

        public AnthropicFetcher(AnthropicClient? client = null)
        {
            _client = client ?? new AnthropicClient();
        }

        public Task<SubscriptionUsage> FetchSubscriptionAsync(ServiceConfig cfg, CancellationToken ct)
        {
            _client.ProxySetting = cfg.Proxy;
            var creds = CredentialReader.Read(new CredentialOptions { ManualToken = cfg.ManualOAuthToken });
            return _client.FetchSubscriptionUsageAsync(creds.AccessToken, creds.Plan, null, ct);
        }

        public Task<ApiRateLimits> FetchRateLimitsAsync(ServiceConfig cfg, CancellationToken ct)
        {
            _client.ProxySetting = cfg.Proxy;
            var model = string.IsNullOrEmpty(cfg.ProbeModel) ? ServiceConfig.DefaultProbeModel : cfg.ProbeModel;
            return _client.ProbeRateLimitsAsync(cfg.ApiKey!, model, null, ct);
        }

        public Task<ApiCost> FetchCostAsync(ServiceConfig cfg, CancellationToken ct)
        {
            _client.ProxySetting = cfg.Proxy;
            return _client.FetchApiCostAsync(cfg.AdminKey!, cfg.MonthlyBudgetUsd, null, ct);
        }

        public void Dispose() => _client.Dispose();
    }

    public sealed class LimitsService : IDisposable
    {
        /// <summary>Hard floors so a misconfiguration can't flood Anthropic's endpoints.</summary>
        public const double MinSubscriptionIntervalSec = 60;
        public const double MinRateLimitIntervalSec = 120;
        public const double MinCostIntervalSec = 120;

        /// <summary>Even a forced refresh waits this long so repeated clicks don't spam the endpoint.</summary>
        public const double ForcedRefreshFloorMs = 15000;

        private readonly Func<ServiceConfig> _getConfig;
        private readonly SharedCache _cache;
        private readonly ILimitsFetcher _fetcher;
        private readonly Func<DateTimeOffset> _now;
        private readonly bool _ownsFetcher;
        private int _running;
        private CancellationTokenSource? _loopCts;
        private Snapshot _last = new Snapshot { UpdatedAt = Iso.EpochZero };

        public LimitsService(
            Func<ServiceConfig> getConfig,
            SharedCache? cache = null,
            ILimitsFetcher? fetcher = null,
            Func<DateTimeOffset>? now = null)
        {
            _getConfig = getConfig ?? throw new ArgumentNullException(nameof(getConfig));
            _cache = cache ?? new SharedCache();
            _ownsFetcher = fetcher == null;
            _fetcher = fetcher ?? new AnthropicFetcher();
            _now = now ?? new Func<DateTimeOffset>(() => DateTimeOffset.UtcNow);
        }

        /// <summary>Raised on a thread-pool thread after every refresh.</summary>
        public event EventHandler<Snapshot>? Updated;

        public Snapshot Snapshot => Volatile.Read(ref _last);

        public static double IntervalMs(string section, ServiceConfig cfg)
        {
            double v, min;
            switch (section)
            {
                case CacheSections.Subscription: v = cfg.SubscriptionIntervalSec; min = MinSubscriptionIntervalSec; break;
                case CacheSections.RateLimits: v = cfg.RateLimitIntervalSec; min = MinRateLimitIntervalSec; break;
                default: v = cfg.CostIntervalSec; min = MinCostIntervalSec; break;
            }
            return Math.Max(min, Js.IsFinite(v) ? v : ServiceConfig.DefaultSubscriptionIntervalSec) * 1000;
        }

        /// <summary>Starts the background loop: one immediate refresh, then one every <paramref name="tickSec"/> seconds.</summary>
        public void Start(double tickSec = 30)
        {
            Stop();
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await RefreshAsync(false, token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        /* never let the loop die */
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(tickSec), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public void Stop()
        {
            var cts = Interlocked.Exchange(ref _loopCts, null);
            if (cts == null) return;
            try { cts.Cancel(); } catch (Exception) { /* ignore */ }
            cts.Dispose();
        }

        /// <summary>force=true ignores the interval (still honours a 429 back-off and the 15 s floor).</summary>
        public async Task<Snapshot> RefreshAsync(bool force = false, CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return Snapshot;
            try
            {
                var cfg = _getConfig();
                var subTask = cfg.SubscriptionEnabled ? SectionAsync(CacheSections.Subscription, cfg, force, ct) : Task.FromResult<CacheEntry?>(null);
                var rlTask = !string.IsNullOrEmpty(cfg.ApiKey) ? SectionAsync(CacheSections.RateLimits, cfg, force, ct) : Task.FromResult<CacheEntry?>(null);
                var costTask = !string.IsNullOrEmpty(cfg.AdminKey) ? SectionAsync(CacheSections.Cost, cfg, force, ct) : Task.FromResult<CacheEntry?>(null);
                await Task.WhenAll(subTask, rlTask, costTask).ConfigureAwait(false);
                var sub = subTask.Result;
                var rl = rlTask.Result;
                var cost = costTask.Result;

                var s = new Snapshot { UpdatedAt = Iso.Format(_now()) };
                if (sub != null)
                {
                    s.Subscription = sub.DataAs<SubscriptionUsage>();
                    if (!string.IsNullOrEmpty(sub.Error)) s.SubscriptionError = sub.Error;
                }
                if (rl != null)
                {
                    s.RateLimits = rl.DataAs<ApiRateLimits>();
                    if (!string.IsNullOrEmpty(rl.Error)) s.RateLimitsError = rl.Error;
                }
                if (cost != null)
                {
                    var c = cost.DataAs<ApiCost>();
                    if (c != null)
                    {
                        // Budget is a local setting: re-apply it so a change shows without refetching.
                        if (cfg.MonthlyBudgetUsd.HasValue && cfg.MonthlyBudgetUsd.Value > 0)
                        {
                            c.BudgetUsd = cfg.MonthlyBudgetUsd.Value;
                            c.PercentOfBudget = c.MonthToDateUsd / cfg.MonthlyBudgetUsd.Value * 100;
                        }
                        else
                        {
                            c.BudgetUsd = null;
                            c.PercentOfBudget = null;
                        }
                        s.Cost = c;
                    }
                    if (!string.IsNullOrEmpty(cost.Error)) s.CostError = cost.Error;
                }
                Volatile.Write(ref _last, s);
                try
                {
                    Updated?.Invoke(this, s);
                }
                catch (Exception)
                {
                    /* UI handler failures must not break polling */
                }
                return s;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static string? KeyFor(string section, ServiceConfig cfg)
        {
            if (section == CacheSections.RateLimits && !string.IsNullOrEmpty(cfg.ApiKey)) return SharedCache.KeyId(cfg.ApiKey!, cfg.ProbeModel ?? "");
            if (section == CacheSections.Cost && !string.IsNullOrEmpty(cfg.AdminKey)) return SharedCache.KeyId(cfg.AdminKey!);
            if (section == CacheSections.Subscription && !string.IsNullOrEmpty(cfg.ManualOAuthToken)) return SharedCache.KeyId(cfg.ManualOAuthToken!);
            return null;
        }

        private async Task<CacheEntry?> SectionAsync(string section, ServiceConfig cfg, bool force, CancellationToken ct)
        {
            var now = _now().ToUnixTimeMilliseconds();
            var wantKey = KeyFor(section, cfg);
            CacheEntry? cached = null;
            try
            {
                cached = _cache.Get(section);
            }
            catch (Exception)
            {
                /* unreadable cache behaves like an empty one */
            }
            var sameKey = cached != null && cached.KeyId == wantKey;

            if (cached != null && sameKey)
            {
                var backoff = Iso.ParseMs(cached.BackoffUntil) ?? 0;
                if (backoff > now) return cached;
                var fetched = Iso.ParseMs(cached.FetchedAt);
                if (fetched.HasValue)
                {
                    double age = now - fetched.Value;
                    var fresh = force ? age < ForcedRefreshFloorMs : age < IntervalMs(section, cfg);
                    if (fresh) return cached;
                }
            }

            var entry = new CacheEntry { FetchedAt = Iso.Format(_now()), KeyId = wantKey };
            try
            {
                entry.Data = await FetchSectionAsync(section, cfg, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                var err = e as LimitsError ?? new LimitsError(LimitsErrorKind.Network, string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message);
                entry.Error = err.Message;
                entry.ErrorKind = err.KindString;
                if (err.Kind == LimitsErrorKind.RateLimited)
                {
                    entry.BackoffUntil = Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(now + (long)((err.RetryAfterSec ?? 300) * 1000)));
                }
                // Keep showing the last good numbers next to the error.
                if (cached != null && cached.HasData && sameKey) entry.Data = cached.Data;
            }
            try
            {
                _cache.Put(section, entry);
            }
            catch (Exception)
            {
                /* cache is best-effort */
            }
            return entry;
        }

        private async Task<JToken> FetchSectionAsync(string section, ServiceConfig cfg, CancellationToken ct)
        {
            switch (section)
            {
                case CacheSections.Subscription:
                    return JsonUtil.ToToken(await _fetcher.FetchSubscriptionAsync(cfg, ct).ConfigureAwait(false));
                case CacheSections.RateLimits:
                    return JsonUtil.ToToken(await _fetcher.FetchRateLimitsAsync(cfg, ct).ConfigureAwait(false));
                default:
                    return JsonUtil.ToToken(await _fetcher.FetchCostAsync(cfg, ct).ConfigureAwait(false));
            }
        }

        public void Dispose()
        {
            Stop();
            if (_ownsFetcher && _fetcher is IDisposable d) d.Dispose();
        }
    }
}
