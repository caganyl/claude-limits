// Mirrors parseSubscriptionUsage() in core/src/parse.ts.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    public sealed class ParsedUsage
    {
        public List<LimitWindow> Windows { get; set; } = new List<LimitWindow>();
        public ExtraUsage? Extra { get; set; }
    }

    public static class UsageParser
    {
        private sealed class FlatMeta
        {
            public FlatMeta(string id, string label, string shortLabel, int order)
            {
                Id = id;
                Label = label;
                ShortLabel = shortLabel;
                Order = order;
            }

            public string Id { get; }
            public string Label { get; }
            public string ShortLabel { get; }
            public int Order { get; }
        }

        /// <summary>Known flat keys of /api/oauth/usage and claude.ai /api/organizations/{id}/usage.</summary>
        private static readonly Dictionary<string, FlatMeta> FlatLabels = new Dictionary<string, FlatMeta>(StringComparer.Ordinal)
        {
            ["five_hour"] = new FlatMeta("session", "Oturum (5 saat)", "5s", 0),
            ["seven_day"] = new FlatMeta("weekly_all", "Haftalık (tüm modeller)", "7g", 1),
            ["seven_day_opus"] = new FlatMeta("weekly_scoped:Opus", "Haftalık · Opus", "7g Opus", 2),
            ["seven_day_sonnet"] = new FlatMeta("weekly_scoped:Sonnet", "Haftalık · Sonnet", "7g Sonnet", 3),
            ["seven_day_oauth_apps"] = new FlatMeta("weekly_oauth_apps", "Haftalık · OAuth uygulamaları", "7g Apps", 4),
            ["seven_day_cowork"] = new FlatMeta("weekly_cowork", "Haftalık · Cowork", "7g Cowork", 5),
        };

        private static readonly HashSet<string> SkipFlatKeys = new HashSet<string>(StringComparer.Ordinal) { "extra_usage", "limits" };

        public static ParsedUsage Parse(string json) => Parse(JsonUtil.Parse(json));

        /// <summary>
        /// Parses the subscription usage payload. The <c>limits[]</c> array (newer) wins; flat keys fill in anything it lacks.
        /// </summary>
        public static ParsedUsage Parse(JToken? payload)
        {
            if (!(payload is JObject obj)) throw new FormatException("Beklenmeyen kullanım yanıtı");
            var structured = ParseLimitsArray(obj["limits"]);
            var flat = ParseFlatKeys(obj);

            // Insertion-ordered map, like JS Map.
            var order = new List<string>();
            var byId = new Dictionary<string, LimitWindow>(StringComparer.Ordinal);
            foreach (var w in structured)
            {
                if (!byId.ContainsKey(w.Id)) order.Add(w.Id);
                byId[w.Id] = w;
            }
            foreach (var w in flat)
            {
                if (byId.ContainsKey(w.Id)) continue;
                order.Add(w.Id);
                byId[w.Id] = w;
            }

            // Stable sort (OrderBy is stable; JS Array.prototype.sort is stable since ES2019).
            var windows = order.Select(id => byId[id]).OrderBy(w => WindowOrder(w)).ToList();
            return new ParsedUsage { Windows = windows, Extra = ParseExtraUsage(obj["extra_usage"]) };
        }

        public static string PrettifyKey(string key)
        {
            return string.Join(" ", key.Split('_').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w));
        }

        private static int WindowOrder(LimitWindow w)
        {
            if (w.Id == "session") return 0;
            if (w.Id == "weekly_all") return 1;
            if (w.Id.StartsWith("weekly_scoped:", StringComparison.Ordinal)) return 2;
            return 3;
        }

        private static List<LimitWindow> ParseLimitsArray(JToken? raw)
        {
            var output = new List<LimitWindow>();
            if (!(raw is JArray arr)) return output;
            foreach (var item in arr)
            {
                if (!(item is JObject entry)) continue;
                var kind = Js.Str(entry["kind"]) ?? "unknown";
                var percent = Js.Num(entry["percent"]) ?? Js.Num(entry["utilization"]);
                if (percent == null) continue;
                var resetsAt = Js.Str(entry["resets_at"]);
                var active = Js.IsTrue(entry["is_active"]);
                string? modelName = null;
                if (entry["scope"] is JObject scope && scope["model"] is JObject model)
                {
                    modelName = Js.Str(model["display_name"]) ?? Js.Str(model["id"]);
                }

                LimitWindow w;
                switch (kind)
                {
                    case "session":
                        w = new LimitWindow { Id = "session", Label = "Oturum (5 saat)", ShortLabel = "5s", Percent = percent.Value };
                        break;
                    case "weekly_all":
                        w = new LimitWindow { Id = "weekly_all", Label = "Haftalık (tüm modeller)", ShortLabel = "7g", Percent = percent.Value };
                        break;
                    case "weekly_scoped":
                        {
                            var name = modelName ?? "Model";
                            w = new LimitWindow { Id = "weekly_scoped:" + name, Label = "Haftalık · " + name, ShortLabel = "7g " + name, Percent = percent.Value };
                            break;
                        }
                    default:
                        {
                            var suffix = modelName != null ? " · " + modelName : "";
                            w = new LimitWindow
                            {
                                Id = kind + (modelName != null ? ":" + modelName : ""),
                                Label = PrettifyKey(kind) + suffix,
                                ShortLabel = kind,
                                Percent = percent.Value,
                            };
                            break;
                        }
                }
                w.Percent = Js.ClampPct(w.Percent);
                if (resetsAt != null) w.ResetsAt = resetsAt;
                if (active) w.Active = true;
                output.Add(w);
            }
            return output;
        }

        private static List<LimitWindow> ParseFlatKeys(JObject json)
        {
            var output = new List<KeyValuePair<int, LimitWindow>>();
            foreach (var prop in json.Properties())
            {
                if (SkipFlatKeys.Contains(prop.Name) || !(prop.Value is JObject value)) continue;
                var percent = Js.Num(value["utilization"]);
                if (percent == null) continue;
                LimitWindow w;
                int ord;
                if (FlatLabels.TryGetValue(prop.Name, out var meta))
                {
                    w = new LimitWindow { Id = meta.Id, Label = meta.Label, ShortLabel = meta.ShortLabel, Percent = Js.ClampPct(percent.Value) };
                    ord = meta.Order;
                }
                else
                {
                    w = new LimitWindow { Id = prop.Name, Label = PrettifyKey(prop.Name), ShortLabel = prop.Name, Percent = Js.ClampPct(percent.Value) };
                    ord = 99;
                }
                var resetsAt = Js.Str(value["resets_at"]);
                if (resetsAt != null) w.ResetsAt = resetsAt;
                output.Add(new KeyValuePair<int, LimitWindow>(ord, w));
            }
            return output.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        }

        private static ExtraUsage? ParseExtraUsage(JToken? raw)
        {
            if (!(raw is JObject obj)) return null;
            var extra = new ExtraUsage { Enabled = Js.IsTrue(obj["is_enabled"]) };
            var pct = Js.Num(obj["utilization"]);
            if (pct != null) extra.Percent = Js.ClampPct(pct.Value);
            // monthly_limit is reported in cents (100000 => $1,000.00).
            var limitCents = Js.Num(obj["monthly_limit"]);
            if (limitCents != null && limitCents.Value > 0) extra.MonthlyLimitUsd = limitCents.Value / 100;
            return extra;
        }
    }
}
