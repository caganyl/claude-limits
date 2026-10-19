// Mirrors the network calls of core/src/api.ts (subscription usage, rate-limit probe, Admin cost/usage).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    public sealed class AnthropicClient : IDisposable
    {
        public const string UserAgent = "claude-limits/1.0 (+https://github.com/caganyl/claude-limits)";
        public const string DefaultApiBase = "https://api.anthropic.com";

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient? _injected;
        private readonly Func<string, string?> _getEnv;
        private readonly object _gate = new object();
        private readonly Dictionary<string, HttpClient> _clients = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);
        private string? _preferredRoute;
        private DateTimeOffset _preferredUntil;

        /// <summary>
        /// "auto" (HTTPS_PROXY/HTTP_PROXY with a direct-connection fallback, otherwise the Windows system proxy),
        /// "direct", or an explicit proxy URL such as http://proxy:8080. Mirrors core/src/net.ts.
        /// </summary>
        public string ProxySetting { get; set; } = "auto";

        /// <summary>The route that last reached the server ("system", "direct" or "proxy:URL").</summary>
        public string? LastRoute { get; private set; }

        static AnthropicClient()
        {
            EnsureModernTls();
        }

        /// <param name="handler">Optional handler (tests); the client is disposed with this instance.</param>
        /// <param name="getEnv">Environment lookup (test seam).</param>
        public AnthropicClient(HttpMessageHandler? handler = null, Func<string, string?>? getEnv = null)
        {
            if (handler != null)
            {
                _injected = new HttpClient(handler, disposeHandler: true);
                // Per-request timeouts are enforced with a CancellationTokenSource (15 s, like api.ts).
                _injected.Timeout = Timeout.InfiniteTimeSpan;
            }
            _getEnv = getEnv ?? new Func<string, string?>(Environment.GetEnvironmentVariable);
        }

        public TimeSpan RequestTimeout { get; set; } = DefaultTimeout;

        /// <summary>
        /// The Visual Studio process runs on .NET Framework. When the process is pinned to explicit protocols
        /// (not SystemDefault = 0), make sure TLS 1.2 (and 1.3 when supported) are included. SystemDefault is
        /// left untouched: the OS already negotiates TLS 1.2+/1.3 and narrowing it would affect all of VS.
        /// </summary>
        public static void EnsureModernTls()
        {
            try
            {
                var current = ServicePointManager.SecurityProtocol;
                if ((int)current == 0) return; // SystemDefault
                if ((current & SecurityProtocolType.Tls12) == 0)
                    ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
                /* not supported on this runtime */
                return;
            }
            try
            {
                // SecurityProtocolType.Tls13 = 12288; not in the netstandard2.0 enum and rejected before .NET Framework 4.8.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
            }
            catch (Exception)
            {
                /* TLS 1.3 unavailable — TLS 1.2 is enough for api.anthropic.com */
            }
        }

        /// <summary>Base URL; CLAUDE_LIMITS_API_BASE overrides it (tests, corporate proxies).</summary>
        public string ApiBase()
        {
            var env = _getEnv("CLAUDE_LIMITS_API_BASE");
            if (env != null)
            {
                var trimmed = env.Trim().TrimEnd('/');
                if (trimmed.Length > 0) return trimmed;
            }
            return DefaultApiBase;
        }

        public void Dispose()
        {
            _injected?.Dispose();
            lock (_gate)
            {
                foreach (var c in _clients.Values) c.Dispose();
                _clients.Clear();
            }
        }

        // ------------------------------------------------------------------ routes (proxy / direct)

        private string? EnvProxy()
        {
            foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
            {
                var v = _getEnv(name);
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }
            return null;
        }

        /// <summary>True when NO_PROXY says the host must not use the proxy.</summary>
        public static bool BypassesProxy(string host, string? noProxy)
        {
            if (string.IsNullOrWhiteSpace(noProxy)) return false;
            var h = host.ToLowerInvariant();
            foreach (var raw in noProxy!.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var e = raw.Trim().ToLowerInvariant();
                var colon = e.LastIndexOf(':');
                if (colon > 0 && e.Substring(colon + 1).All(char.IsDigit)) e = e.Substring(0, colon);
                if (e == "*") return true;
                var bare = e.TrimStart('*').TrimStart('.');
                if (h == bare || h.EndsWith("." + bare, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string NormalizeProxy(string u) => u.Contains("://") ? u : "http://" + u;

        /// <summary>Routes to try, in order (same rules as routesFor in core/src/net.ts).</summary>
        public List<string> RoutesFor(string host)
        {
            var setting = (ProxySetting ?? "auto").Trim();
            if (setting.Equals("direct", StringComparison.OrdinalIgnoreCase)) return new List<string> { "direct" };
            if (setting.Length > 0 && !setting.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return new List<string> { "proxy:" + NormalizeProxy(setting) };
            var env = EnvProxy();
            if (env == null) return new List<string> { "system" };
            if (BypassesProxy(host, _getEnv("NO_PROXY") ?? _getEnv("no_proxy"))) return new List<string> { "direct" };
            return new List<string> { "proxy:" + NormalizeProxy(env), "direct" };
        }

        private HttpClient ClientFor(string route)
        {
            lock (_gate)
            {
                if (_clients.TryGetValue(route, out var existing)) return existing;
                var handler = new HttpClientHandler();
                if (route == "direct")
                {
                    handler.UseProxy = false;
                }
                else if (route.StartsWith("proxy:", StringComparison.Ordinal))
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(new Uri(route.Substring(6))) { UseDefaultCredentials = true };
                }
                // "system": default handler = Windows/IE proxy settings (incl. PAC).
                var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
                _clients[route] = client;
                return client;
            }
        }

        // ------------------------------------------------------------------ plumbing

        private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
        {
            if (_injected != null)
                return await SendOnceAsync(_injected, makeRequest(), ct).ConfigureAwait(false);

            var probe = makeRequest();
            var host = probe.RequestUri?.Host ?? "api.anthropic.com";
            var routes = RoutesFor(host);
            lock (_gate)
            {
                if (_preferredRoute != null && _preferredUntil > DateTimeOffset.UtcNow && routes.Count > 1 && routes.Contains(_preferredRoute))
                {
                    routes.Remove(_preferredRoute);
                    routes.Insert(0, _preferredRoute);
                }
            }

            LimitsError? last = null;
            for (var i = 0; i < routes.Count; i++)
            {
                var request = i == 0 ? probe : makeRequest();
                try
                {
                    var res = await SendOnceAsync(ClientFor(routes[i]), request, ct).ConfigureAwait(false);
                    LastRoute = routes[i];
                    if (routes.Count > 1)
                        lock (_gate)
                        {
                            _preferredRoute = routes[i];
                            _preferredUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                        }
                    return res;
                }
                catch (RouteFailure rf)
                {
                    last = rf.Error;
                    if (!rf.TryNextRoute) break;
                }
            }
            lock (_gate) _preferredRoute = null;
            throw last ?? new LimitsError(LimitsErrorKind.Network, "Ağ hatası");
        }

        /// <summary>Wraps a network error with whether another route is worth trying.</summary>
        private sealed class RouteFailure : Exception
        {
            public RouteFailure(LimitsError error, bool tryNext) : base(error.Message) { Error = error; TryNextRoute = tryNext; }
            public LimitsError Error { get; }
            public bool TryNextRoute { get; }
        }

        private async Task<HttpResponseMessage> SendOnceAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
        {
            using (var timeout = new CancellationTokenSource(RequestTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            {
                try
                {
                    return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    var err = new LimitsError(LimitsErrorKind.Network, "Ağ hatası: Zaman aşımı (15 sn içinde yanıt yok) — ağ veya proxy ayarını kontrol edin");
                    if (_injected != null) throw err;
                    throw new RouteFailure(err, true);
                }
                catch (Exception e) when (e is HttpRequestException || e is WebException)
                {
                    var err = new LimitsError(LimitsErrorKind.Network, "Ağ hatası: " + DescribeNetworkError(e));
                    if (_injected != null) throw err;
                    throw new RouteFailure(err, !IsCertificateError(e));
                }
            }
        }

        private static WebExceptionStatus? WebStatus(Exception e)
        {
            for (var cur = e; cur != null; cur = cur.InnerException)
                if (cur is WebException we) return we.Status;
            return null;
        }

        private static bool IsCertificateError(Exception e)
        {
            var st = WebStatus(e);
            if (st == WebExceptionStatus.TrustFailure || st == WebExceptionStatus.SecureChannelFailure) return true;
            for (var cur = e; cur != null; cur = cur.InnerException)
                if (cur is System.Security.Authentication.AuthenticationException) return true;
            return false;
        }

        /// <summary>Same idea as describeNetworkError in core/src/api.ts.</summary>
        public static string DescribeNetworkError(Exception e)
        {
            var detail = InnermostMessage(e);
            var st = WebStatus(e);
            string? hint = null;
            if (IsCertificateError(e)) hint = "Şirket SSL sertifikası tanınmıyor";
            else if (st == WebExceptionStatus.NameResolutionFailure) hint = "Sunucu adı çözülemedi — ağ/proxy ayarını kontrol edin";
            else if (st == WebExceptionStatus.ProxyNameResolutionFailure) hint = "Proxy sunucusunun adı çözülemedi";
            else if (st == WebExceptionStatus.ConnectFailure || st == WebExceptionStatus.ConnectionClosed || st == WebExceptionStatus.Timeout)
                hint = "Bağlantı kurulamadı — proxy/güvenlik duvarı engelliyor olabilir";
            return hint != null ? hint + " (" + detail + ")" : detail;
        }

        private static string InnermostMessage(Exception e)
        {
            var msg = e.Message;
            var inner = e.InnerException;
            while (inner != null)
            {
                if (!string.IsNullOrEmpty(inner.Message)) msg = inner.Message;
                inner = inner.InnerException;
            }
            return string.IsNullOrEmpty(msg) ? e.GetType().Name : msg;
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage res)
        {
            try
            {
                return res.Content != null ? await res.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string ErrorText(int status, string body)
        {
            try
            {
                if (JsonUtil.Parse(body) is JObject obj)
                {
                    var err = obj["error"];
                    if (err != null && err.Type == JTokenType.String) return err.Value<string>() ?? "HTTP " + status;
                    if (err is JObject eo)
                    {
                        var m = Js.Str(eo["message"]);
                        if (m != null) return m;
                    }
                    var top = Js.Str(obj["message"]);
                    if (top != null) return top;
                }
            }
            catch (Exception)
            {
                /* not JSON */
            }
            return "HTTP " + status;
        }

        private static double? RetryAfter(HttpResponseMessage res)
        {
            var delta = res.Headers.RetryAfter?.Delta;
            if (delta.HasValue && delta.Value.TotalSeconds > 0) return delta.Value.TotalSeconds;
            var raw = Header(res, "retry-after");
            var v = Js.NumFromString(raw);
            return v.HasValue && v.Value > 0 ? v : null;
        }

        /// <summary>Case-insensitive header lookup across response and content headers; null when missing.</summary>
        internal static string? Header(HttpResponseMessage res, string name)
        {
            if (res.Headers.TryGetValues(name, out var values)) return string.Join(", ", values);
            if (res.Content != null && res.Content.Headers.TryGetValues(name, out var cvalues)) return string.Join(", ", cvalues);
            return null;
        }

        private static async Task<LimitsError> ErrorFor(HttpResponseMessage res, string authHint)
        {
            var status = (int)res.StatusCode;
            var text = ErrorText(status, await ReadBodyAsync(res).ConfigureAwait(false));
            if (status == 401 || status == 403) return new LimitsError(LimitsErrorKind.Auth, authHint + " (" + text + ")");
            if (status == 429) return new LimitsError(LimitsErrorKind.RateLimited, "Çok sık istek; biraz sonra tekrar denenecek", RetryAfter(res) ?? 300);
            if (status >= 500) return new LimitsError(LimitsErrorKind.Server, "Sunucu hatası: " + text);
            return new LimitsError(LimitsErrorKind.BadRequest, text);
        }

        private static void AddCommonHeaders(HttpRequestMessage req)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        // ------------------------------------------------------------------ subscription

        /// <summary>Subscription limits via the Claude Code OAuth token (same data as <c>/usage</c> in Claude Code).</summary>
        public async Task<SubscriptionUsage> FetchSubscriptionUsageAsync(string accessToken, string? plan = null, DateTimeOffset? now = null, CancellationToken ct = default)
        {
            HttpRequestMessage Make()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, ApiBase() + "/api/oauth/usage");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                AddCommonHeaders(req);
                return req;
            }
            {
                using (var res = await SendAsync(Make, ct).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode)
                        throw await ErrorFor(res, "Claude Code oturumu geçersiz; terminalde `claude` çalıştırıp giriş yapın").ConfigureAwait(false);
                    var parsed = UsageParser.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var usage = new SubscriptionUsage
                    {
                        Windows = parsed.Windows,
                        Extra = parsed.Extra,
                        Source = "claude-code",
                        FetchedAt = Iso.Format(now ?? DateTimeOffset.UtcNow),
                    };
                    if (!string.IsNullOrEmpty(plan)) usage.Plan = plan;
                    return usage;
                }
            }
        }

        // ------------------------------------------------------------------ rate limits

        /// <summary>
        /// Reads the per-minute API rate limits by sending the smallest possible Messages request
        /// (1 output token). Costs a tiny fraction of a cent per probe; limits are per model class.
        /// </summary>
        public async Task<ApiRateLimits> ProbeRateLimitsAsync(string apiKey, string model, DateTimeOffset? now = null, CancellationToken ct = default)
        {
            var body = new JObject
            {
                ["model"] = model,
                ["max_tokens"] = 1,
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "." }),
            };
            var json = body.ToString(Newtonsoft.Json.Formatting.None);
            HttpRequestMessage Make()
            {
                var req = new HttpRequestMessage(HttpMethod.Post, ApiBase() + "/v1/messages");
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                AddCommonHeaders(req);
                req.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
                return req;
            }
            {
                using (var res = await SendAsync(Make, ct).ConfigureAwait(false))
                {
                    var limited = (int)res.StatusCode == 429;
                    if (!res.IsSuccessStatusCode && !limited)
                        throw await ErrorFor(res, "API anahtarı geçersiz").ConfigureAwait(false);
                    var result = RateLimitParser.Parse(h => Header(res, h), model, limited, now);
                    // Drain the body so the connection can be reused.
                    await ReadBodyAsync(res).ConfigureAwait(false);
                    if (result.Buckets.Count == 0 && !limited)
                        throw new LimitsError(LimitsErrorKind.Server, "Yanıtta rate-limit başlıkları yok");
                    return result;
                }
            }
        }

        // ------------------------------------------------------------------ Admin API

        private static DateTimeOffset StartOfUtcDay(DateTimeOffset d)
        {
            var u = d.UtcDateTime;
            return new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero);
        }

        private static string Query(IEnumerable<KeyValuePair<string, string>> pairs) =>
            string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        private async Task<JToken> AdminGetAsync(string adminKey, string path, List<KeyValuePair<string, string>> query, CancellationToken ct)
        {
            var url = ApiBase() + path + "?" + Query(query);
            HttpRequestMessage Make()
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-api-key", adminKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                AddCommonHeaders(req);
                return req;
            }
            {
                using (var res = await SendAsync(Make, ct).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode)
                        throw await ErrorFor(res, "Admin API anahtarı geçersiz (sk-ant-admin… gerekli)").ConfigureAwait(false);
                    return JsonUtil.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }

        /// <summary>Month-to-date and today's spend (and today's tokens) via the Admin API (sk-ant-admin…).</summary>
        public async Task<ApiCost> FetchApiCostAsync(string adminKey, double? budgetUsd = null, DateTimeOffset? now = null, CancellationToken ct = default)
        {
            var n = now ?? DateTimeOffset.UtcNow;
            var today = StartOfUtcDay(n);
            var monthStart = new DateTimeOffset(n.UtcDateTime.Year, n.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var tomorrow = today.AddDays(1);
            var todayMs = today.ToUnixTimeMilliseconds();

            // Cost: daily buckets from the 1st of the month (UTC) up to the end of today.
            double monthToDate = 0;
            double todayUsd = 0;
            string? page = null;
            for (var i = 0; i < 10; i++)
            {
                var p = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("starting_at", Iso.Format(monthStart)),
                    new KeyValuePair<string, string>("ending_at", Iso.Format(tomorrow)),
                    new KeyValuePair<string, string>("bucket_width", "1d"),
                    new KeyValuePair<string, string>("limit", "31"),
                };
                if (page != null) p.Add(new KeyValuePair<string, string>("page", page));
                var parsed = CostParser.ParseCostReportPage(await AdminGetAsync(adminKey, "/v1/organizations/cost_report", p, ct).ConfigureAwait(false));
                foreach (var kv in parsed.PerDay)
                {
                    monthToDate += kv.Value;
                    if (Iso.ParseMs(kv.Key) == todayMs) todayUsd += kv.Value;
                }
                if (!parsed.HasMore || parsed.NextPage == null) break;
                page = parsed.NextPage;
            }

            // Tokens: today only.
            var tokens = new TokenTotals();
            page = null;
            for (var i = 0; i < 10; i++)
            {
                var p = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("starting_at", Iso.Format(today)),
                    new KeyValuePair<string, string>("ending_at", Iso.Format(tomorrow)),
                    new KeyValuePair<string, string>("bucket_width", "1d"),
                    new KeyValuePair<string, string>("limit", "1"),
                };
                if (page != null) p.Add(new KeyValuePair<string, string>("page", page));
                var parsed = CostParser.ParseUsageReportPage(await AdminGetAsync(adminKey, "/v1/organizations/usage_report/messages", p, ct).ConfigureAwait(false));
                tokens.Add(parsed.Totals);
                if (!parsed.HasMore || parsed.NextPage == null) break;
                page = parsed.NextPage;
            }

            var cost = new ApiCost
            {
                MonthToDateUsd = monthToDate,
                TodayUsd = todayUsd,
                TokensToday = tokens,
                FetchedAt = Iso.Format(n),
            };
            if (budgetUsd.HasValue && budgetUsd.Value > 0)
            {
                cost.BudgetUsd = budgetUsd.Value;
                cost.PercentOfBudget = monthToDate / budgetUsd.Value * 100;
            }
            return cost;
        }
    }
}
