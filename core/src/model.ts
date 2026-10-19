// Shared data model for every Claude Limits client (VS Code, tray app, browser).
// The Visual Studio (C#) extension mirrors these shapes in Models.cs.

export type Level = 'ok' | 'warn' | 'crit' | 'unknown';

/** One rolling subscription window (5-hour session, weekly, per-model weekly...). */
export interface LimitWindow {
  /** Stable id: "session", "weekly_all", "weekly_scoped:Opus", or a raw API key. */
  id: string;
  /** Long label shown in panels, e.g. "Haftalık · Opus". */
  label: string;
  /** Short label for status bars, e.g. "7g Opus". */
  shortLabel: string;
  /** 0–100. */
  percent: number;
  /** ISO-8601 reset time, when the API returns one. */
  resetsAt?: string;
  /** True when Anthropic marks this window as the one currently binding the account. */
  active?: boolean;
  /**
   * True for buckets Anthropic hasn't documented (internal codenames such as "iguana_necktie").
   * Shown separately and never used for the status bar, icon colour or notifications.
   */
  undocumented?: boolean;
}

export interface ExtraUsage {
  enabled: boolean;
  /** 0–100, when known. */
  percent?: number;
  /** Monthly cap in USD, when known. */
  monthlyLimitUsd?: number;
}

export interface SubscriptionUsage {
  windows: LimitWindow[];
  extra?: ExtraUsage;
  /** Plan name from the local credentials (e.g. "max", "pro"), when known. */
  plan?: string;
  /** Where the numbers came from. */
  source: 'claude-code' | 'claude-web';
  fetchedAt: string;
}

export type RateLimitBucketId = 'requests' | 'tokens' | 'input_tokens' | 'output_tokens';

export interface RateLimitBucket {
  id: RateLimitBucketId;
  label: string;
  limit: number;
  remaining: number;
  /** 0–100, used share of the per-minute allowance. */
  percentUsed: number;
  resetsAt?: string;
}

export interface ApiRateLimits {
  model: string;
  buckets: RateLimitBucket[];
  /** True when the probe itself was answered with HTTP 429. */
  limited: boolean;
  fetchedAt: string;
}

export interface TokenTotals {
  uncachedInput: number;
  cacheRead: number;
  cacheWrite: number;
  output: number;
}

export interface ApiCost {
  monthToDateUsd: number;
  todayUsd: number;
  budgetUsd?: number;
  /** monthToDate / budget, 0–100+. */
  percentOfBudget?: number;
  tokensToday?: TokenTotals;
  fetchedAt: string;
}

export interface Snapshot {
  subscription?: SubscriptionUsage;
  subscriptionError?: string;
  rateLimits?: ApiRateLimits;
  rateLimitsError?: string;
  cost?: ApiCost;
  costError?: string;
  updatedAt: string;
}

export interface Thresholds {
  warn: number;
  crit: number;
}

export const DEFAULT_THRESHOLDS: Thresholds = { warn: 75, crit: 90 };
