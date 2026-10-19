import { strict as assert } from 'node:assert';
import { mkdtempSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import {
  LimitsError,
  LimitsService,
  SharedCache,
  compactText,
  fetchApiCost,
  fetchSubscriptionUsage,
  formatResetIn,
  headlinePercent,
  keyId,
  keychainServices,
  levelOf,
  parseCostReportPage,
  parseRateLimitHeaders,
  parseSubscriptionUsage,
  pickOrg,
  probeRateLimits,
  readClaudeCodeCredentials,
  snapshotLevel,
} from '../src/index';

// ---------------------------------------------------------------- fixtures
const NEW_SHAPE = {
  five_hour: { utilization: 29.0, resets_at: '2026-10-19T14:00:00+00:00' },
  seven_day: { utilization: 26.0, resets_at: '2026-10-23T08:00:00+00:00' },
  seven_day_opus: null,
  seven_day_sonnet: null,
  seven_day_oauth_apps: null,
  iguana_necktie: null,
  extra_usage: { is_enabled: true, monthly_limit: 5000, used_credits: 120.0, utilization: 2.4 },
  limits: [
    { kind: 'session', percent: 29, resets_at: '2026-10-19T14:00:00+00:00', scope: null, is_active: false },
    { kind: 'weekly_all', percent: 26, resets_at: '2026-10-23T08:00:00+00:00', scope: null, is_active: false },
    {
      kind: 'weekly_scoped',
      percent: 43,
      resets_at: '2026-10-22T10:00:00+00:00',
      scope: { model: { id: null, display_name: 'Fable' } },
      is_active: true,
    },
  ],
};

const OLD_SHAPE = {
  five_hour: { utilization: 35.0, resets_at: '2026-02-06T22:00:00+00:00' },
  seven_day: { utilization: 14.0, resets_at: '2026-02-12T20:00:00+00:00' },
  seven_day_sonnet: { utilization: 39.0, resets_at: '2026-02-09T14:00:00+00:00' },
  seven_day_opus: null,
  mystery_bucket: { utilization: 7, resets_at: null },
  extra_usage: { is_enabled: false, monthly_limit: null, used_credits: null, utilization: null },
};

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
}

// ---------------------------------------------------------------- parsing
test('limits[] array wins and keeps the per-model weekly cap', () => {
  const r = parseSubscriptionUsage(NEW_SHAPE);
  assert.deepEqual(
    r.windows.map((w) => [w.id, w.percent, !!w.active]),
    [
      ['session', 29, false],
      ['weekly_all', 26, false],
      ['weekly_scoped:Fable', 43, true],
    ],
  );
  assert.equal(r.windows[2].label, 'Haftalık · Fable');
  assert.equal(r.windows[2].resetsAt, '2026-10-22T10:00:00+00:00');
  assert.deepEqual(r.extra, { enabled: true, percent: 2.4, monthlyLimitUsd: 50 });
});

test('flat keys are parsed when limits[] is absent; null buckets skipped, unknown kept', () => {
  const r = parseSubscriptionUsage(OLD_SHAPE);
  assert.deepEqual(
    r.windows.map((w) => w.id),
    ['session', 'weekly_all', 'weekly_scoped:Sonnet', 'mystery_bucket'],
  );
  assert.equal(r.windows[0].shortLabel, '5s');
  assert.equal(r.windows[3].label, 'Mystery Bucket');
  assert.equal(r.windows[3].resetsAt, undefined);
  assert.deepEqual(r.extra, { enabled: false });
});

test('percent is clamped to 0..100 and junk payload throws', () => {
  const r = parseSubscriptionUsage({ five_hour: { utilization: 130 } });
  assert.equal(r.windows[0].percent, 100);
  assert.throws(() => parseSubscriptionUsage(null));
  assert.deepEqual(parseSubscriptionUsage({}).windows, []);
});

test('rate-limit headers become buckets', () => {
  const h: Record<string, string> = {
    'anthropic-ratelimit-requests-limit': '50',
    'anthropic-ratelimit-requests-remaining': '40',
    'anthropic-ratelimit-requests-reset': '2026-10-19T11:21:00Z',
    'anthropic-ratelimit-input-tokens-limit': '30000',
    'anthropic-ratelimit-input-tokens-remaining': '30000',
    'anthropic-ratelimit-output-tokens-limit': '8000',
    'anthropic-ratelimit-output-tokens-remaining': '2000',
  };
  const r = parseRateLimitHeaders((n) => h[n], 'claude-haiku-5-5', false, new Date('2026-10-19T11:20:00Z'));
  assert.deepEqual(
    r.buckets.map((b) => [b.id, b.percentUsed]),
    [
      ['requests', 20],
      ['input_tokens', 0],
      ['output_tokens', 75],
    ],
  );
  assert.equal(r.buckets[0].resetsAt, '2026-10-19T11:21:00Z');
});

test('cost report: cents strings summed per day, non-USD ignored', () => {
  const p = parseCostReportPage({
    data: [
      { starting_at: '2026-10-01T00:00:00Z', results: [{ currency: 'USD', amount: '150.5' }, { currency: 'USD', amount: '49.5' }] },
      { starting_at: '2026-10-19T00:00:00Z', results: [{ currency: 'USD', amount: '75' }, { currency: 'EUR', amount: '999' }] },
    ],
    has_more: true,
    next_page: 'page_2',
  });
  assert.equal(p.perDay.get('2026-10-01T00:00:00Z'), 2);
  assert.equal(p.perDay.get('2026-10-19T00:00:00Z'), 0.75);
  assert.equal(p.nextPage, 'page_2');
});

// ---------------------------------------------------------------- formatting
test('reset countdown in Turkish', () => {
  const now = new Date('2026-10-19T11:45:00Z');
  assert.equal(formatResetIn('2026-10-19T14:00:00Z', now), '2 sa 15 dk');
  assert.equal(formatResetIn('2026-10-22T16:00:00Z', now), '3 gün 4 sa');
  assert.equal(formatResetIn('2026-10-19T12:00:00Z', now), '15 dk');
  assert.equal(formatResetIn('2026-10-19T11:45:25Z', now), '25 sn');
  assert.equal(formatResetIn('2026-10-19T11:00:00Z', now), 'şimdi');
  assert.equal(formatResetIn(undefined, now), '');
});

test('levels, compact text and headline', () => {
  assert.equal(levelOf(74.9), 'ok');
  assert.equal(levelOf(75), 'warn');
  assert.equal(levelOf(90), 'crit');
  assert.equal(levelOf(undefined), 'unknown');
  const sub = { ...parseSubscriptionUsage(NEW_SHAPE), source: 'claude-code' as const, fetchedAt: '' };
  const snap = { subscription: sub, updatedAt: '' };
  // Binding per-model weekly cap (Fable 43%) replaces the all-model weekly (26%).
  assert.equal(compactText(snap, 'both'), '5s 29% · 7g Fable 43%');
  assert.equal(compactText(snap, 'session'), '5s 29%');
  assert.equal(headlinePercent(snap, 'both'), 43);
  assert.equal(snapshotLevel(snap, { warn: 40, crit: 90 }), 'warn');
  assert.equal(compactText({ updatedAt: '' }), '');
});

test('keyId is a stable sha256 prefix (shared with the C# extension)', () => {
  assert.equal(keyId('test-key'), '62af8704764f');
  assert.equal(keyId('test-key', 'claude-haiku-5-5'), '62af8704764f|claude-haiku-5-5');
});

test('keychain service names for default and custom config dirs', () => {
  assert.deepEqual(keychainServices(undefined), ['Claude Code-credentials']);
  const s = keychainServices('/Users/me/.claude-work');
  assert.equal(s.length, 2);
  assert.match(s[0], /^Claude Code-credentials-[0-9a-f]{8}$/);
});

test('pickOrg prefers last active, then chat-capable', () => {
  const orgs = [
    { uuid: 'a', name: 'API', capabilities: ['api'] },
    { uuid: 'b', name: 'Me', capabilities: ['chat', 'claude_max'] },
  ];
  assert.equal(pickOrg(orgs)?.uuid, 'b');
  assert.equal(pickOrg(orgs, 'a')?.uuid, 'a');
  assert.equal(pickOrg(orgs, 'zzz')?.uuid, 'b');
});

// ---------------------------------------------------------------- credentials
test('credentials: file on Windows/Linux, expiry detection, env + manual override', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'cl-creds-'));
  const now = new Date('2026-10-19T12:00:00Z');
  writeFileSync(
    join(dir, '.credentials.json'),
    JSON.stringify({ claudeAiOauth: { accessToken: 'tok-file', expiresAt: now.getTime() + 3600e3, subscriptionType: 'max' } }),
  );
  const c = await readClaudeCodeCredentials({ configDir: dir, platform: 'win32', env: {}, now });
  assert.equal(c.accessToken, 'tok-file');
  assert.equal(c.plan, 'max');

  const later = new Date(now.getTime() + 2 * 3600e3);
  await assert.rejects(readClaudeCodeCredentials({ configDir: dir, platform: 'linux', env: {}, now: later }), (e: unknown) => {
    return e instanceof LimitsError && e.kind === 'expired';
  });

  const env = await readClaudeCodeCredentials({ configDir: dir, platform: 'linux', env: { CLAUDE_CODE_OAUTH_TOKEN: 'tok-env' }, now });
  assert.equal(env.accessToken, 'tok-env');
  const manual = await readClaudeCodeCredentials({ manualToken: ' tok-manual ', configDir: dir, env: { CLAUDE_CODE_OAUTH_TOKEN: 'x' }, now });
  assert.equal(manual.accessToken, 'tok-manual');

  const empty = mkdtempSync(join(tmpdir(), 'cl-empty-'));
  await assert.rejects(readClaudeCodeCredentials({ configDir: empty, platform: 'linux', env: {}, now }), (e: unknown) => {
    return e instanceof LimitsError && e.kind === 'no_credentials';
  });
});

test('credentials: macOS keychain is tried first, file is the fallback', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'cl-mac-'));
  const now = new Date('2026-10-19T12:00:00Z');
  const asked: string[] = [];
  const kc = async (svc: string) => {
    asked.push(svc);
    return svc === 'Claude Code-credentials'
      ? JSON.stringify({ claudeAiOauth: { accessToken: 'tok-kc', expiresAt: now.getTime() + 1e6 } })
      : null;
  };
  const c = await readClaudeCodeCredentials({ platform: 'darwin', env: {}, now, readKeychain: kc, configDir: undefined });
  assert.equal(c.accessToken, 'tok-kc');
  assert.match(c.source, /Anahtar Zinciri/);

  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, '.credentials.json'), JSON.stringify({ claudeAiOauth: { accessToken: 'tok-file2' } }));
  const c2 = await readClaudeCodeCredentials({ platform: 'darwin', env: {}, now, readKeychain: async () => null, configDir: dir });
  assert.equal(c2.accessToken, 'tok-file2');
});

// ---------------------------------------------------------------- HTTP
test('fetchSubscriptionUsage sends the OAuth headers and maps 401/429', async () => {
  let seen: Request | undefined;
  const ok = (async (url: string, init: RequestInit) => {
    seen = new Request(url, init);
    return jsonResponse(NEW_SHAPE);
  }) as typeof fetch;
  const r = await fetchSubscriptionUsage('tok', { fetchFn: ok, plan: 'pro' });
  assert.equal(seen?.url, 'https://api.anthropic.com/api/oauth/usage');
  assert.equal(seen?.headers.get('authorization'), 'Bearer tok');
  assert.equal(seen?.headers.get('anthropic-beta'), 'oauth-2025-04-20');
  assert.equal(r.windows.length, 3);
  assert.equal(r.plan, 'pro');

  const unauth = (async () => jsonResponse({ error: { message: 'invalid token' } }, 401)) as typeof fetch;
  await assert.rejects(fetchSubscriptionUsage('tok', { fetchFn: unauth }), (e: unknown) => (e as LimitsError).kind === 'auth');
  const limited = (async () => jsonResponse({}, 429, { 'retry-after': '120' })) as typeof fetch;
  await assert.rejects(fetchSubscriptionUsage('tok', { fetchFn: limited }), (e: unknown) => {
    const le = e as LimitsError;
    return le.kind === 'rate_limited' && le.retryAfterSec === 120;
  });
});

test('probeRateLimits reads headers on 200 and 429, errors on 401', async () => {
  let body: unknown;
  const h = {
    'anthropic-ratelimit-requests-limit': '50',
    'anthropic-ratelimit-requests-remaining': '0',
  };
  const ok = (async (_u: string, init: RequestInit) => {
    body = JSON.parse(String(init.body));
    return jsonResponse({ content: [] }, 200, h);
  }) as typeof fetch;
  const r = await probeRateLimits('sk', 'claude-haiku-5-5', { fetchFn: ok });
  assert.deepEqual(body, { model: 'claude-haiku-5-5', max_tokens: 1, messages: [{ role: 'user', content: '.' }] });
  assert.equal(r.buckets[0].percentUsed, 100);
  assert.equal(r.limited, false);

  const lim = (async () => jsonResponse({ error: {} }, 429, h)) as typeof fetch;
  assert.equal((await probeRateLimits('sk', 'm', { fetchFn: lim })).limited, true);

  const bad = (async () => jsonResponse({ error: { message: 'invalid x-api-key' } }, 401)) as typeof fetch;
  await assert.rejects(probeRateLimits('sk', 'm', { fetchFn: bad }), /invalid x-api-key/);
});

test('fetchApiCost follows pagination and computes month/today/budget', async () => {
  const urls: string[] = [];
  const f = (async (url: string) => {
    urls.push(url);
    const u = new URL(url);
    if (u.pathname.endsWith('cost_report')) {
      if (!u.searchParams.get('page')) {
        return jsonResponse({
          data: [{ starting_at: '2026-10-01T00:00:00Z', results: [{ currency: 'USD', amount: '1000' }] }],
          has_more: true,
          next_page: 'p2',
        });
      }
      return jsonResponse({
        data: [{ starting_at: '2026-10-19T00:00:00Z', results: [{ currency: 'USD', amount: '250' }] }],
        has_more: false,
      });
    }
    return jsonResponse({
      data: [
        {
          starting_at: '2026-10-19T00:00:00Z',
          results: [
            { uncached_input_tokens: 100, cache_read_input_tokens: 50, output_tokens: 20, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 5 } },
          ],
        },
      ],
      has_more: false,
    });
  }) as typeof fetch;
  const c = await fetchApiCost('sk-ant-admin', { fetchFn: f, budgetUsd: 50, now: new Date('2026-10-19T15:00:00Z') });
  assert.equal(c.monthToDateUsd, 12.5);
  assert.equal(c.todayUsd, 2.5);
  assert.equal(c.percentOfBudget, 25);
  assert.deepEqual(c.tokensToday, { uncachedInput: 100, cacheRead: 50, cacheWrite: 10, output: 20 });
  const first = new URL(urls[0]);
  assert.equal(first.searchParams.get('starting_at'), '2026-10-01T00:00:00.000Z');
  assert.equal(first.searchParams.get('ending_at'), '2026-10-20T00:00:00.000Z');
  assert.equal(new URL(urls[1]).searchParams.get('page'), 'p2');
});

// ---------------------------------------------------------------- service + shared cache
test('service: shares results through the cache file and respects intervals/back-off', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'cl-cache-'));
  const credDir = mkdtempSync(join(tmpdir(), 'cl-cred-'));
  writeFileSync(join(credDir, '.credentials.json'), JSON.stringify({ claudeAiOauth: { accessToken: 'tok' } }));
  const prevDir = process.env.CLAUDE_CONFIG_DIR;
  const prevTok = process.env.CLAUDE_CODE_OAUTH_TOKEN;
  process.env.CLAUDE_CONFIG_DIR = credDir;
  delete process.env.CLAUDE_CODE_OAUTH_TOKEN;
  const realFetch = globalThis.fetch;
  let calls = 0;
  let status = 200;
  globalThis.fetch = (async () => {
    calls++;
    return status === 200 ? jsonResponse(NEW_SHAPE) : jsonResponse({}, status, { 'retry-after': '600' });
  }) as typeof fetch;
  try {
    let clock = new Date('2026-10-19T12:00:00Z');
    const cfg = {
      subscriptionEnabled: true,
      probeModel: 'm',
      subscriptionIntervalSec: 180,
      rateLimitIntervalSec: 900,
      costIntervalSec: 600,
    };
    const cache = new SharedCache(dir);
    const a = new LimitsService(() => cfg, () => {}, cache, () => clock);
    const b = new LimitsService(() => cfg, () => {}, cache, () => clock); // a second editor window

    const s1 = await a.refresh();
    assert.equal(calls, 1);
    assert.equal(s1.subscription?.windows.length, 3);
    await b.refresh(); // served from the shared cache
    assert.equal(calls, 1);

    clock = new Date(clock.getTime() + 10_000);
    await b.refresh(true); // forced, but within 15 s floor
    assert.equal(calls, 1);

    clock = new Date(clock.getTime() + 200_000); // past the 180 s interval
    status = 429;
    const s2 = await b.refresh();
    assert.equal(calls, 2);
    assert.match(s2.subscriptionError ?? '', /Çok sık/);
    assert.equal(s2.subscription?.windows.length, 3, 'last good data is kept');

    clock = new Date(clock.getTime() + 300_000); // still inside the 600 s back-off
    status = 200;
    await a.refresh(true);
    assert.equal(calls, 2);

    clock = new Date(clock.getTime() + 400_000);
    const s3 = await a.refresh();
    assert.equal(calls, 3);
    assert.equal(s3.subscriptionError, undefined);
  } finally {
    globalThis.fetch = realFetch;
    if (prevDir === undefined) delete process.env.CLAUDE_CONFIG_DIR;
    else process.env.CLAUDE_CONFIG_DIR = prevDir;
    if (prevTok !== undefined) process.env.CLAUDE_CODE_OAUTH_TOKEN = prevTok;
  }
});
