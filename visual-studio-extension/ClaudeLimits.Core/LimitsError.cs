using System;

namespace ClaudeLimits.Core
{
    /// <summary>Error categories; <see cref="LimitsError.KindString"/> gives the shared errorKind string.</summary>
    public enum LimitsErrorKind
    {
        Auth,
        RateLimited,
        Network,
        Server,
        BadRequest,
        NoCredentials,
        Expired,
    }

    /// <summary>Mirrors api.ts LimitsError. Message is user-facing Turkish text.</summary>
    public sealed class LimitsError : Exception
    {
        public LimitsError(LimitsErrorKind kind, string message, double? retryAfterSec = null)
            : base(message)
        {
            Kind = kind;
            RetryAfterSec = retryAfterSec;
        }

        public LimitsErrorKind Kind { get; }

        /// <summary>Seconds to wait before retrying (from retry-after), when known.</summary>
        public double? RetryAfterSec { get; }

        /// <summary>"auth" | "rate_limited" | "network" | "server" | "bad_request" | "no_credentials" | "expired".</summary>
        public string KindString => ToKindString(Kind);

        public static string ToKindString(LimitsErrorKind kind)
        {
            switch (kind)
            {
                case LimitsErrorKind.Auth: return "auth";
                case LimitsErrorKind.RateLimited: return "rate_limited";
                case LimitsErrorKind.Network: return "network";
                case LimitsErrorKind.Server: return "server";
                case LimitsErrorKind.BadRequest: return "bad_request";
                case LimitsErrorKind.NoCredentials: return "no_credentials";
                case LimitsErrorKind.Expired: return "expired";
                default: return "network";
            }
        }

        public static LimitsErrorKind? FromKindString(string? s)
        {
            switch (s)
            {
                case "auth": return LimitsErrorKind.Auth;
                case "rate_limited": return LimitsErrorKind.RateLimited;
                case "network": return LimitsErrorKind.Network;
                case "server": return LimitsErrorKind.Server;
                case "bad_request": return LimitsErrorKind.BadRequest;
                case "no_credentials": return LimitsErrorKind.NoCredentials;
                case "expired": return LimitsErrorKind.Expired;
                default: return null;
            }
        }
    }
}
