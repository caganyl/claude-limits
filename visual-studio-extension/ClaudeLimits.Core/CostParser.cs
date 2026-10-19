// Mirrors parseCostReportPage() / parseUsageReportPage() in core/src/parse.ts.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    public sealed class CostPage
    {
        /// <summary>USD per bucket start (ISO string, as returned by the API). Insertion-ordered.</summary>
        public List<KeyValuePair<string, double>> PerDay { get; } = new List<KeyValuePair<string, double>>();
        public bool HasMore { get; set; }
        public string? NextPage { get; set; }
    }

    public sealed class UsagePage
    {
        public TokenTotals Totals { get; } = new TokenTotals();
        public bool HasMore { get; set; }
        public string? NextPage { get; set; }
    }

    public static class CostParser
    {
        public static CostPage ParseCostReportPage(string json) => ParseCostReportPage(JsonUtil.Parse(json));

        /// <summary>Parses one page of <c>/v1/organizations/cost_report</c>. Amounts are decimal strings in cents.</summary>
        public static CostPage ParseCostReportPage(JToken? payload)
        {
            if (!(payload is JObject obj)) throw new FormatException("Beklenmeyen maliyet yanıtı");
            var page = new CostPage();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            if (obj["data"] is JArray data)
            {
                foreach (var item in data)
                {
                    if (!(item is JObject bucket)) continue;
                    var start = Js.Str(bucket["starting_at"]) ?? "";
                    double cents = 0;
                    if (bucket["results"] is JArray results)
                    {
                        foreach (var rt in results)
                        {
                            if (!(rt is JObject r)) continue;
                            var currency = Js.Str(r["currency"]) ?? "USD";
                            if (currency != "USD") continue;
                            cents += Js.Num(r["amount"]) ?? 0;
                        }
                    }
                    if (index.TryGetValue(start, out var i))
                    {
                        page.PerDay[i] = new KeyValuePair<string, double>(start, page.PerDay[i].Value + cents / 100);
                    }
                    else
                    {
                        index[start] = page.PerDay.Count;
                        page.PerDay.Add(new KeyValuePair<string, double>(start, cents / 100));
                    }
                }
            }
            page.HasMore = Js.IsTrue(obj["has_more"]);
            page.NextPage = Js.Str(obj["next_page"]);
            return page;
        }

        public static UsagePage ParseUsageReportPage(string json) => ParseUsageReportPage(JsonUtil.Parse(json));

        /// <summary>Sums token fields from one page of <c>/v1/organizations/usage_report/messages</c>.</summary>
        public static UsagePage ParseUsageReportPage(JToken? payload)
        {
            if (!(payload is JObject obj)) throw new FormatException("Beklenmeyen kullanım raporu");
            var page = new UsagePage();
            var t = page.Totals;
            if (obj["data"] is JArray data)
            {
                foreach (var item in data)
                {
                    if (!(item is JObject bucket) || !(bucket["results"] is JArray results)) continue;
                    foreach (var rt in results)
                    {
                        if (!(rt is JObject r)) continue;
                        t.UncachedInput += Js.Num(r["uncached_input_tokens"]) ?? 0;
                        t.CacheRead += Js.Num(r["cache_read_input_tokens"]) ?? 0;
                        t.Output += Js.Num(r["output_tokens"]) ?? 0;
                        if (r["cache_creation"] is JObject cc)
                        {
                            foreach (var p in cc.Properties()) t.CacheWrite += Js.Num(p.Value) ?? 0;
                        }
                        else
                        {
                            t.CacheWrite += Js.Num(r["cache_creation_input_tokens"]) ?? 0;
                        }
                    }
                }
            }
            page.HasMore = Js.IsTrue(obj["has_more"]);
            page.NextPage = Js.Str(obj["next_page"]);
            return page;
        }
    }
}
