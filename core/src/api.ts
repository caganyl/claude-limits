// Network calls. Uses the global fetch, so it runs in Node 18+, Electron and browser extensions.
import type { ApiCost, ApiRateLimits, SubscriptionUsage } from './model';
import { parseCostReportPage, parseRateLimitHeaders, parseSubscriptionUsage, parseUsageReportPage } from './parse';

export const USER_AGENT = 'claude-limits/1.0 (+https://github.com/caganyl/claude-limits)';
/** Base URL; CLAUDE_LIMITS_API_BASE overrides it (tests, corporate proxies). */
function apiBase(): string {
  const env = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env?.CLAUDE_LIMITS_API_BASE;
  return (env && env.trim().replace(/\/+$/, '')) || 'https://api.anthropic.com';
}

export type ErrorKind = 'auth' | 'rate_limited' | 'network' | 'server' | 'bad_request' | 'no_credentials' | 'expired';

export class LimitsError extends Error {
  constructor(
    public readonly kind: ErrorKind,
    message: string,
    /** Seconds to wait before retrying (from retry-after), when known. */
    public readonly retryAfterSec?: number,
  ) {
    super(message);
    this.name = 'LimitsError';
  }
}

type FetchFn = typeof fetch;

async function doFetch(fetchFn: FetchFn, url: string, init: RequestInit, timeoutMs = 15000): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetchFn(url, { ...init, signal: ctrl.signal });
  } catch (e) {
    const msg = (e as Error)?.name === 'AbortError' ? 'Zaman aşımı' : (e as Error)?.message || String(e);
    throw new LimitsError('network', `Ağ hatası: ${msg}`);
  } finally {
    clearTimeout(timer);
  }
}

async function errorText(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { error?: { message?: string } | string; message?: string };
    if (typeof body.error === 'string') return body.error;
    return body.error?.message || body.message || `HTTP ${res.status}`;
  } catch {
    return `HTTP ${res.status}`;
  }
}

function retryAfter(res: Response): number | undefined {
  const v = Number(res.headers.get('retry-after'));
  return Number.isFinite(v) && v > 0 ? v : undefined;
}

async function raiseFor(res: Response, authHint: string): Promise<never> {
  const text = await errorText(res);
  if (res.status === 401 || res.status === 403) throw new LimitsError('auth', `${authHint} (${text})`);
  if (res.status === 429) throw new LimitsError('rate_limited', 'Çok sık istek; biraz sonra tekrar denenecek', retryAfter(res) ?? 300);
  if (res.status >= 500) throw new LimitsError('server', `Sunucu hatası: ${text}`);
  throw new LimitsError('bad_request', text);
}

/** Subscription limits via the Claude Code OAuth token (same data as `/usage` in Claude Code). */
export async function fetchSubscriptionUsage(
  accessToken: string,
  opts: { plan?: string; fetchFn?: FetchFn; now?: Date } = {},
): Promise<SubscriptionUsage> {
  const fetchFn = opts.fetchFn ?? fetch;
  const res = await doFetch(fetchFn, `${apiBase()}/api/oauth/usage`, {
    method: 'GET',
    headers: {
      Authorization: `Bearer ${accessToken}`,
      'anthropic-beta': 'oauth-2025-04-20',
      'Content-Type': 'application/json',
      'User-Agent': USER_AGENT,
    },
  });
  if (!res.ok) await raiseFor(res, 'Claude Code oturumu geçersiz; terminalde `claude` çalıştırıp giriş yapın');
  const parsed = parseSubscriptionUsage(await res.json());
  return {
    ...parsed,
    ...(opts.plan ? { plan: opts.plan } : {}),
    source: 'claude-code',
    fetchedAt: (opts.now ?? new Date()).toISOString(),
  };
}

/**
 * Reads the per-minute API rate limits by sending the smallest possible Messages request
 * (1 output token). Costs a tiny fraction of a cent per probe; limits are per model class.
 */
export async function probeRateLimits(
  apiKey: string,
  model: string,
  opts: { fetchFn?: FetchFn; now?: Date; browser?: boolean } = {},
): Promise<ApiRateLimits> {
  const fetchFn = opts.fetchFn ?? fetch;
  const headers: Record<string, string> = {
    'x-api-key': apiKey,
    'anthropic-version': '2023-06-01',
    'content-type': 'application/json',
  };
  if (opts.browser) headers['anthropic-dangerous-direct-browser-access'] = 'true';
  else headers['User-Agent'] = USER_AGENT;
  const res = await doFetch(fetchFn, `${apiBase()}/v1/messages`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ model, max_tokens: 1, messages: [{ role: 'user', content: '.' }] }),
  });
  const limited = res.status === 429;
  if (!res.ok && !limited) await raiseFor(res, 'API anahtarı geçersiz');
  const result = parseRateLimitHeaders((h) => res.headers.get(h), model, limited, opts.now);
  // Drain the body so the connection can be reused.
  try { await res.arrayBuffer(); } catch { /* ignore */ }
  if (!result.buckets.length && !limited) {
    throw new LimitsError('server', 'Yanıtta rate-limit başlıkları yok');
  }
  return result;
}

function startOfUtcDay(d: Date): Date {
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
}

/** Month-to-date and today's spend (and today's tokens) via the Admin API (sk-ant-admin…). */
export async function fetchApiCost(
  adminKey: string,
  opts: { budgetUsd?: number; fetchFn?: FetchFn; now?: Date; browser?: boolean } = {},
): Promise<ApiCost> {
  const fetchFn = opts.fetchFn ?? fetch;
  const now = opts.now ?? new Date();
  const today = startOfUtcDay(now);
  const monthStart = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const tomorrow = new Date(today.getTime() + 86400000);
  const headers: Record<string, string> = { 'x-api-key': adminKey, 'anthropic-version': '2023-06-01' };
  if (opts.browser) headers['anthropic-dangerous-direct-browser-access'] = 'true';
  else headers['User-Agent'] = USER_AGENT;

  const get = async (path: string, params: URLSearchParams) => {
    const res = await doFetch(fetchFn, `${apiBase()}${path}?${params.toString()}`, { method: 'GET', headers });
    if (!res.ok) await raiseFor(res, 'Admin API anahtarı geçersiz (sk-ant-admin… gerekli)');
    return res.json();
  };

  // Cost: daily buckets from the 1st of the month (UTC) up to the end of today.
  let monthToDate = 0;
  let todayUsd = 0;
  let page: string | undefined;
  for (let i = 0; i < 10; i++) {
    const p = new URLSearchParams({
      starting_at: monthStart.toISOString(),
      ending_at: tomorrow.toISOString(),
      bucket_width: '1d',
      limit: '31',
    });
    if (page) p.set('page', page);
    const parsed = parseCostReportPage(await get('/v1/organizations/cost_report', p));
    for (const [start, usd] of parsed.perDay) {
      monthToDate += usd;
      if (Date.parse(start) === today.getTime()) todayUsd += usd;
    }
    if (!parsed.hasMore || !parsed.nextPage) break;
    page = parsed.nextPage;
  }

  // Tokens: today only.
  const tokens = { uncachedInput: 0, cacheRead: 0, cacheWrite: 0, output: 0 };
  page = undefined;
  for (let i = 0; i < 10; i++) {
    const p = new URLSearchParams({
      starting_at: today.toISOString(),
      ending_at: tomorrow.toISOString(),
      bucket_width: '1d',
      limit: '1',
    });
    if (page) p.set('page', page);
    const parsed = parseUsageReportPage(await get('/v1/organizations/usage_report/messages', p));
    tokens.uncachedInput += parsed.totals.uncachedInput;
    tokens.cacheRead += parsed.totals.cacheRead;
    tokens.cacheWrite += parsed.totals.cacheWrite;
    tokens.output += parsed.totals.output;
    if (!parsed.hasMore || !parsed.nextPage) break;
    page = parsed.nextPage;
  }

  const cost: ApiCost = {
    monthToDateUsd: monthToDate,
    todayUsd,
    tokensToday: tokens,
    fetchedAt: now.toISOString(),
  };
  if (opts.budgetUsd && opts.budgetUsd > 0) {
    cost.budgetUsd = opts.budgetUsd;
    cost.percentOfBudget = (monthToDate / opts.budgetUsd) * 100;
  }
  return cost;
}

/** claude.ai web session (browser extension only): lists orgs and reads usage with cookies. */
export async function fetchClaudeWebUsage(
  orgId: string,
  opts: { fetchFn?: FetchFn; now?: Date } = {},
): Promise<SubscriptionUsage> {
  const fetchFn = opts.fetchFn ?? fetch;
  const res = await doFetch(fetchFn, `https://claude.ai/api/organizations/${encodeURIComponent(orgId)}/usage`, {
    method: 'GET',
    credentials: 'include',
    headers: { Accept: 'application/json' },
  });
  if (!res.ok) await raiseFor(res, 'claude.ai oturumu yok; tarayıcıda claude.ai’ye giriş yapın');
  const parsed = parseSubscriptionUsage(await res.json());
  return { ...parsed, source: 'claude-web', fetchedAt: (opts.now ?? new Date()).toISOString() };
}

export interface ClaudeOrg {
  uuid: string;
  name: string;
  capabilities: string[];
}

export async function fetchClaudeWebOrgs(opts: { fetchFn?: FetchFn } = {}): Promise<ClaudeOrg[]> {
  const fetchFn = opts.fetchFn ?? fetch;
  const res = await doFetch(fetchFn, 'https://claude.ai/api/organizations', {
    method: 'GET',
    credentials: 'include',
    headers: { Accept: 'application/json' },
  });
  if (!res.ok) await raiseFor(res, 'claude.ai oturumu yok; tarayıcıda claude.ai’ye giriş yapın');
  const body = (await res.json()) as unknown;
  if (!Array.isArray(body)) return [];
  return body
    .filter((o): o is Record<string, unknown> => typeof o === 'object' && o !== null)
    .map((o) => ({
      uuid: String(o.uuid ?? ''),
      name: String(o.name ?? ''),
      capabilities: Array.isArray(o.capabilities) ? o.capabilities.map(String) : [],
    }))
    .filter((o) => o.uuid);
}

/** Picks the org to read: the last active one if known, else the first chat-capable one. */
export function pickOrg(orgs: ClaudeOrg[], lastActive?: string): ClaudeOrg | undefined {
  if (lastActive) {
    const hit = orgs.find((o) => o.uuid === lastActive);
    if (hit) return hit;
  }
  return orgs.find((o) => o.capabilities.includes('chat')) ?? orgs[0];
}
