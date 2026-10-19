// Node-only: polls every source on its own interval, shares results through the cache file,
// and hands a merged Snapshot to the UI.
import { LimitsError, fetchApiCost, fetchSubscriptionUsage, probeRateLimits } from './api';
import { CacheEntry, CacheSection, SharedCache, keyId } from './cache';
import { readClaudeCodeCredentials } from './credentials';
import type { ApiCost, ApiRateLimits, Snapshot, SubscriptionUsage } from './model';

export interface ServiceConfig {
  /** Show subscription (Pro/Max/Team) limits from the Claude Code login. */
  subscriptionEnabled: boolean;
  /** Optional token pasted by the user instead of the Claude Code login. */
  manualOAuthToken?: string;
  /** Standard API key (sk-ant-api…) for per-minute rate limits. */
  apiKey?: string;
  /** Model whose rate limits are probed. */
  probeModel: string;
  /** Admin API key (sk-ant-admin…) for spend & tokens. */
  adminKey?: string;
  monthlyBudgetUsd?: number;
  subscriptionIntervalSec: number;
  rateLimitIntervalSec: number;
  costIntervalSec: number;
}

export const DEFAULT_CONFIG: ServiceConfig = {
  subscriptionEnabled: true,
  probeModel: 'claude-haiku-5-5',
  subscriptionIntervalSec: 180,
  rateLimitIntervalSec: 900,
  costIntervalSec: 600,
};

/** Hard floors so a misconfiguration can't flood Anthropic's endpoints. */
const MIN_INTERVAL_SEC: Record<CacheSection, number> = { subscription: 60, rateLimits: 120, cost: 120 };

function intervalFor(section: CacheSection, cfg: ServiceConfig): number {
  const v =
    section === 'subscription' ? cfg.subscriptionIntervalSec : section === 'rateLimits' ? cfg.rateLimitIntervalSec : cfg.costIntervalSec;
  return Math.max(MIN_INTERVAL_SEC[section], Number.isFinite(v) ? v : DEFAULT_CONFIG.subscriptionIntervalSec) * 1000;
}

export class LimitsService {
  private timer?: ReturnType<typeof setInterval>;
  private running = false;
  private last: Snapshot = { updatedAt: new Date(0).toISOString() };

  constructor(
    private readonly getConfig: () => ServiceConfig,
    private readonly onUpdate: (s: Snapshot) => void,
    private readonly cache: SharedCache = new SharedCache(),
    private readonly now: () => Date = () => new Date(),
  ) {}

  get snapshot(): Snapshot {
    return this.last;
  }

  start(tickSec = 30): void {
    this.stop();
    void this.refresh(false);
    this.timer = setInterval(() => void this.refresh(false), tickSec * 1000);
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = undefined;
  }

  /** force=true ignores the interval (still honours a 429 back-off). */
  async refresh(force = false): Promise<Snapshot> {
    if (this.running) return this.last;
    this.running = true;
    try {
      const cfg = this.getConfig();
      const [sub, rl, cost] = await Promise.all([
        cfg.subscriptionEnabled ? this.section<SubscriptionUsage>('subscription', cfg, force) : undefined,
        cfg.apiKey ? this.section<ApiRateLimits>('rateLimits', cfg, force) : undefined,
        cfg.adminKey ? this.section<ApiCost>('cost', cfg, force) : undefined,
      ]);
      const s: Snapshot = { updatedAt: this.now().toISOString() };
      if (sub?.data) s.subscription = sub.data;
      if (sub?.error) s.subscriptionError = sub.error;
      if (rl?.data) s.rateLimits = rl.data;
      if (rl?.error) s.rateLimitsError = rl.error;
      if (cost?.data) {
        // Budget is a local setting: re-apply it so a change shows without refetching.
        const c = { ...cost.data };
        if (cfg.monthlyBudgetUsd && cfg.monthlyBudgetUsd > 0) {
          c.budgetUsd = cfg.monthlyBudgetUsd;
          c.percentOfBudget = (c.monthToDateUsd / cfg.monthlyBudgetUsd) * 100;
        } else {
          delete c.budgetUsd;
          delete c.percentOfBudget;
        }
        s.cost = c;
      }
      if (cost?.error) s.costError = cost.error;
      this.last = s;
      this.onUpdate(s);
      return s;
    } finally {
      this.running = false;
    }
  }

  private keyFor(section: CacheSection, cfg: ServiceConfig): string | undefined {
    if (section === 'rateLimits' && cfg.apiKey) return keyId(cfg.apiKey, cfg.probeModel);
    if (section === 'cost' && cfg.adminKey) return keyId(cfg.adminKey);
    if (section === 'subscription' && cfg.manualOAuthToken) return keyId(cfg.manualOAuthToken);
    return undefined;
  }

  private async section<T>(section: CacheSection, cfg: ServiceConfig, force: boolean): Promise<CacheEntry<T>> {
    const now = this.now().getTime();
    const wantKey = this.keyFor(section, cfg);
    const cached = await this.cache.get<T>(section);
    const sameKey = cached && (cached.keyId ?? undefined) === wantKey;

    if (cached && sameKey) {
      const backoff = cached.backoffUntil ? Date.parse(cached.backoffUntil) : 0;
      if (backoff > now) return cached;
      const age = now - Date.parse(cached.fetchedAt);
      // Even a forced refresh waits 15 s so repeated clicks don't spam the endpoint.
      const fresh = force ? age < 15000 : age < intervalFor(section, cfg);
      if (fresh) return cached;
    }

    const entry: CacheEntry<T> = { fetchedAt: this.now().toISOString(), ...(wantKey ? { keyId: wantKey } : {}) };
    try {
      entry.data = (await this.fetchSection(section, cfg)) as T;
    } catch (e) {
      const err = e instanceof LimitsError ? e : new LimitsError('network', (e as Error)?.message ?? String(e));
      entry.error = err.message;
      entry.errorKind = err.kind;
      if (err.kind === 'rate_limited') {
        entry.backoffUntil = new Date(now + (err.retryAfterSec ?? 300) * 1000).toISOString();
      }
      // Keep showing the last good numbers next to the error.
      if (cached?.data !== undefined && sameKey) entry.data = cached.data;
    }
    try {
      await this.cache.put(section, entry);
    } catch {
      /* cache is best-effort */
    }
    return entry;
  }

  private async fetchSection(section: CacheSection, cfg: ServiceConfig): Promise<unknown> {
    switch (section) {
      case 'subscription': {
        const creds = await readClaudeCodeCredentials({ manualToken: cfg.manualOAuthToken });
        return fetchSubscriptionUsage(creds.accessToken, { plan: creds.plan });
      }
      case 'rateLimits':
        return probeRateLimits(cfg.apiKey!, cfg.probeModel || DEFAULT_CONFIG.probeModel);
      case 'cost':
        return fetchApiCost(cfg.adminKey!, { budgetUsd: cfg.monthlyBudgetUsd });
    }
  }
}
