// Pure parsers — no Node APIs, safe to bundle into the browser extension.
import type {
  ApiRateLimits,
  ExtraUsage,
  LimitWindow,
  RateLimitBucket,
  RateLimitBucketId,
  TokenTotals,
} from './model';

type Json = Record<string, unknown>;

const isObj = (v: unknown): v is Json => typeof v === 'object' && v !== null && !Array.isArray(v);
const num = (v: unknown): number | undefined => {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v))) return Number(v);
  return undefined;
};
const str = (v: unknown): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined);
const clampPct = (p: number) => Math.max(0, Math.min(100, p));

/** Known flat keys of /api/oauth/usage and claude.ai /api/organizations/{id}/usage. */
const FLAT_LABELS: Record<string, { id: string; label: string; short: string; order: number }> = {
  five_hour: { id: 'session', label: 'Oturum (5 saat)', short: '5s', order: 0 },
  seven_day: { id: 'weekly_all', label: 'Haftalık (tüm modeller)', short: '7g', order: 1 },
  seven_day_opus: { id: 'weekly_scoped:Opus', label: 'Haftalık · Opus', short: '7g Opus', order: 2 },
  seven_day_sonnet: { id: 'weekly_scoped:Sonnet', label: 'Haftalık · Sonnet', short: '7g Sonnet', order: 3 },
  seven_day_oauth_apps: { id: 'weekly_oauth_apps', label: 'Haftalık · OAuth uygulamaları', short: '7g Apps', order: 4 },
  seven_day_cowork: { id: 'weekly_cowork', label: 'Haftalık · Cowork', short: '7g Cowork', order: 5 },
};

const SKIP_FLAT_KEYS = new Set(['extra_usage', 'limits']);

function prettifyKey(key: string): string {
  return key
    .split('_')
    .map((w) => (w ? w[0].toUpperCase() + w.slice(1) : w))
    .join(' ');
}

function windowOrder(w: LimitWindow): number {
  if (w.id === 'session') return 0;
  if (w.id === 'weekly_all') return 1;
  if (w.id.startsWith('weekly_scoped:')) return 2;
  return 3;
}

/** Parses the newer structured `limits` array. Returns [] when absent/empty. */
function parseLimitsArray(raw: unknown): LimitWindow[] {
  if (!Array.isArray(raw)) return [];
  const out: LimitWindow[] = [];
  for (const entry of raw) {
    if (!isObj(entry)) continue;
    const kind = str(entry.kind) ?? 'unknown';
    const percent = num(entry.percent) ?? num(entry.utilization);
    if (percent === undefined) continue;
    const resetsAt = str(entry.resets_at);
    const active = entry.is_active === true;
    let modelName: string | undefined;
    if (isObj(entry.scope) && isObj(entry.scope.model)) {
      modelName = str(entry.scope.model.display_name) ?? str(entry.scope.model.id);
    }
    let w: LimitWindow;
    switch (kind) {
      case 'session':
        w = { id: 'session', label: 'Oturum (5 saat)', shortLabel: '5s', percent };
        break;
      case 'weekly_all':
        w = { id: 'weekly_all', label: 'Haftalık (tüm modeller)', shortLabel: '7g', percent };
        break;
      case 'weekly_scoped': {
        const name = modelName ?? 'Model';
        w = { id: `weekly_scoped:${name}`, label: `Haftalık · ${name}`, shortLabel: `7g ${name}`, percent };
        break;
      }
      default: {
        const name = modelName ? ` · ${modelName}` : '';
        w = { id: `${kind}${modelName ? ':' + modelName : ''}`, label: `${prettifyKey(kind)}${name}`, shortLabel: kind, percent };
      }
    }
    w.percent = clampPct(w.percent);
    if (resetsAt) w.resetsAt = resetsAt;
    if (active) w.active = true;
    out.push(w);
  }
  return out;
}

/** Parses the older flat keys (five_hour, seven_day, ...). Unknown keys with a numeric utilization are kept. */
function parseFlatKeys(json: Json): LimitWindow[] {
  const out: Array<LimitWindow & { _o: number }> = [];
  for (const [key, value] of Object.entries(json)) {
    if (SKIP_FLAT_KEYS.has(key) || !isObj(value)) continue;
    const percent = num(value.utilization);
    if (percent === undefined) continue;
    const meta = FLAT_LABELS[key];
    const w: LimitWindow & { _o: number } = meta
      ? { id: meta.id, label: meta.label, shortLabel: meta.short, percent: clampPct(percent), _o: meta.order }
      : { id: key, label: prettifyKey(key), shortLabel: key, percent: clampPct(percent), _o: 99 };
    const resetsAt = str(value.resets_at);
    if (resetsAt) w.resetsAt = resetsAt;
    out.push(w);
  }
  out.sort((a, b) => a._o - b._o);
  return out.map(({ _o, ...w }) => w);
}

function parseExtraUsage(raw: unknown): ExtraUsage | undefined {
  if (!isObj(raw)) return undefined;
  const enabled = raw.is_enabled === true;
  const extra: ExtraUsage = { enabled };
  const pct = num(raw.utilization);
  if (pct !== undefined) extra.percent = clampPct(pct);
  // monthly_limit is reported in cents (100000 => $1,000.00).
  const limitCents = num(raw.monthly_limit);
  if (limitCents !== undefined && limitCents > 0) extra.monthlyLimitUsd = limitCents / 100;
  return extra;
}

/**
 * Parses the subscription usage payload. Works for both
 * `GET https://api.anthropic.com/api/oauth/usage` and
 * `GET https://claude.ai/api/organizations/{org}/usage` (same shape).
 *
 * The `limits[]` array (newer) wins; flat keys fill in anything it lacks.
 */
export function parseSubscriptionUsage(payload: unknown): { windows: LimitWindow[]; extra?: ExtraUsage } {
  if (!isObj(payload)) throw new Error('Beklenmeyen kullanım yanıtı');
  const structured = parseLimitsArray(payload.limits);
  const flat = parseFlatKeys(payload);
  const byId = new Map<string, LimitWindow>();
  for (const w of structured) byId.set(w.id, w);
  for (const w of flat) if (!byId.has(w.id)) byId.set(w.id, w);
  const windows = [...byId.values()].sort((a, b) => windowOrder(a) - windowOrder(b));
  const extra = parseExtraUsage(payload.extra_usage);
  return extra ? { windows, extra } : { windows };
}

// ---------------------------------------------------------------- API rate limits

const BUCKETS: Array<{ id: RateLimitBucketId; label: string }> = [
  { id: 'requests', label: 'İstek / dk' },
  { id: 'input_tokens', label: 'Girdi token / dk' },
  { id: 'output_tokens', label: 'Çıktı token / dk' },
  { id: 'tokens', label: 'Toplam token / dk' },
];

/** Builds rate-limit buckets from `anthropic-ratelimit-*` response headers. */
export function parseRateLimitHeaders(
  getHeader: (name: string) => string | null | undefined,
  model: string,
  limited: boolean,
  now: Date = new Date(),
): ApiRateLimits {
  const buckets: RateLimitBucket[] = [];
  for (const b of BUCKETS) {
    const limit = num(getHeader(`anthropic-ratelimit-${b.id.replace('_', '-')}-limit`));
    const remaining = num(getHeader(`anthropic-ratelimit-${b.id.replace('_', '-')}-remaining`));
    if (limit === undefined || remaining === undefined || limit <= 0) continue;
    const resetsAt = getHeader(`anthropic-ratelimit-${b.id.replace('_', '-')}-reset`) ?? undefined;
    buckets.push({
      id: b.id,
      label: b.label,
      limit,
      remaining,
      percentUsed: clampPct(((limit - remaining) / limit) * 100),
      ...(resetsAt ? { resetsAt } : {}),
    });
  }
  return { model, buckets, limited, fetchedAt: now.toISOString() };
}

// ---------------------------------------------------------------- Admin API (cost & usage)

export interface CostPage {
  /** USD per bucket start (ISO). */
  perDay: Map<string, number>;
  hasMore: boolean;
  nextPage?: string;
}

/** Parses one page of `/v1/organizations/cost_report`. Amounts are decimal strings in cents. */
export function parseCostReportPage(payload: unknown): CostPage {
  if (!isObj(payload)) throw new Error('Beklenmeyen maliyet yanıtı');
  const perDay = new Map<string, number>();
  const data = Array.isArray(payload.data) ? payload.data : [];
  for (const bucket of data) {
    if (!isObj(bucket)) continue;
    const start = str(bucket.starting_at) ?? '';
    let cents = 0;
    const results = Array.isArray(bucket.results) ? bucket.results : [];
    for (const r of results) {
      if (!isObj(r)) continue;
      const currency = str(r.currency) ?? 'USD';
      if (currency !== 'USD') continue;
      cents += num(r.amount) ?? 0;
    }
    perDay.set(start, (perDay.get(start) ?? 0) + cents / 100);
  }
  return { perDay, hasMore: payload.has_more === true, nextPage: str(payload.next_page) };
}

/** Sums token fields from one page of `/v1/organizations/usage_report/messages`. */
export function parseUsageReportPage(payload: unknown): { totals: TokenTotals; hasMore: boolean; nextPage?: string } {
  if (!isObj(payload)) throw new Error('Beklenmeyen kullanım raporu');
  const totals: TokenTotals = { uncachedInput: 0, cacheRead: 0, cacheWrite: 0, output: 0 };
  const data = Array.isArray(payload.data) ? payload.data : [];
  for (const bucket of data) {
    if (!isObj(bucket) || !Array.isArray(bucket.results)) continue;
    for (const r of bucket.results) {
      if (!isObj(r)) continue;
      totals.uncachedInput += num(r.uncached_input_tokens) ?? 0;
      totals.cacheRead += num(r.cache_read_input_tokens) ?? 0;
      totals.output += num(r.output_tokens) ?? 0;
      if (isObj(r.cache_creation)) {
        for (const v of Object.values(r.cache_creation)) totals.cacheWrite += num(v) ?? 0;
      } else {
        totals.cacheWrite += num(r.cache_creation_input_tokens) ?? 0;
      }
    }
  }
  return { totals, hasMore: payload.has_more === true, nextPage: str(payload.next_page) };
}
