// JSON + time helpers that reproduce JavaScript semantics (Number(), Date.parse, toISOString,
// Math.round) closely enough for the parsers and the shared cache file.
using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.Core
{
    internal static class Js
    {
        public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>JavaScript Math.round: halves round towards +∞.</summary>
        public static double Round(double v) => Math.Floor(v + 0.5);

        public static double ClampPct(double p) => Math.Max(0, Math.Min(100, p));

        /// <summary>Mirrors parse.ts num(): finite JSON numbers or numeric, non-blank strings.</summary>
        public static double? Num(JToken? v)
        {
            if (v == null) return null;
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    {
                        double d;
                        try { d = v.Value<double>(); }
                        catch (Exception) { return null; }
                        return IsFinite(d) ? d : (double?)null;
                    }
                case JTokenType.String:
                    return NumFromString(v.Value<string>());
                default:
                    return null;
            }
        }

        public static double? NumFromString(string? s)
        {
            if (s == null) return null;
            var t = s.Trim();
            if (t.Length == 0) return null;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && IsFinite(d)) return d;
            return null;
        }

        /// <summary>Mirrors parse.ts str(): non-empty strings only.</summary>
        public static string? Str(JToken? v)
        {
            if (v == null || v.Type != JTokenType.String) return null;
            var s = v.Value<string>();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public static JObject? Obj(JToken? v) => v as JObject;

        public static bool IsTrue(JToken? v) => v != null && v.Type == JTokenType.Boolean && v.Value<bool>();
    }

    /// <summary>ISO-8601 helpers matching JavaScript's Date.parse / toISOString.</summary>
    public static class Iso
    {
        public const string EpochZero = "1970-01-01T00:00:00.000Z";

        /// <summary>Same output as JavaScript's Date.prototype.toISOString().</summary>
        public static string Format(DateTimeOffset t) =>
            t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        public static bool TryParse(string? s, out DateTimeOffset value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTimeOffset.TryParse(
                s!.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out value);
        }

        /// <summary>Date.parse equivalent in epoch milliseconds; null instead of NaN.</summary>
        public static long? ParseMs(string? s) => TryParse(s, out var v) ? v.ToUnixTimeMilliseconds() : (long?)null;
    }

    public static class JsonUtil
    {
        /// <summary>Serializer settings shared by the cache file and tests (no date parsing, JS-like numbers).</summary>
        public static JsonSerializerSettings Settings { get; } = CreateSettings();

        public static JsonSerializer Serializer { get; } = JsonSerializer.Create(Settings);

        private static JsonSerializerSettings CreateSettings()
        {
            var s = new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None,
                NullValueHandling = NullValueHandling.Ignore,
                FloatParseHandling = FloatParseHandling.Double,
                Formatting = Newtonsoft.Json.Formatting.Indented,
                MissingMemberHandling = MissingMemberHandling.Ignore,
            };
            s.Converters.Add(new JsNumberConverter());
            return s;
        }

        /// <summary>Parses JSON without turning ISO strings into DateTime tokens.</summary>
        public static JToken Parse(string json)
        {
            using (var sr = new StringReader(json))
            using (var reader = new JsonTextReader(sr))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                var token = JToken.ReadFrom(reader);
                // Reject trailing garbage like JSON.parse does.
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException("Unexpected content after JSON value.");
                }
                return token;
            }
        }

        /// <summary>JSON.stringify(value, null, 2) equivalent.</summary>
        public static string Stringify(JToken token)
        {
            using (var sw = new StringWriter(CultureInfo.InvariantCulture))
            {
                using (var writer = new JsonTextWriter(sw))
                {
                    writer.Formatting = Newtonsoft.Json.Formatting.Indented;
                    writer.Indentation = 2;
                    writer.IndentChar = ' ';
                    token.WriteTo(writer);
                }
                return sw.ToString().Replace("\r\n", "\n");
            }
        }

        public static JToken ToToken(object value) => JToken.FromObject(value, Serializer);

        public static T? ToObject<T>(JToken token) where T : class => token.ToObject<T>(Serializer);
    }

    /// <summary>Writes whole doubles as integers (42 instead of 42.0) like JSON.stringify.</summary>
    internal sealed class JsNumberConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(double) || objectType == typeof(double?);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null || reader.TokenType == JsonToken.Undefined)
                return objectType == typeof(double?) ? (object?)null : 0d;
            var raw = reader.Value;
            if (raw is string s)
            {
                var n = Js.NumFromString(s);
                if (n.HasValue) return n.Value;
                return objectType == typeof(double?) ? (object?)null : 0d;
            }
            if (raw == null) return objectType == typeof(double?) ? (object?)null : 0d;
            return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }
            var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!Js.IsFinite(d))
            {
                writer.WriteNull(); // JSON.stringify(NaN) === "null"
                return;
            }
            if (Math.Floor(d) == d && Math.Abs(d) < 9007199254740992d)
                writer.WriteValue((long)d);
            else
                writer.WriteValue(d);
        }
    }
}
