// MV3 service worker: polls claude.ai (with the browser's own login) and, optionally, the
// Anthropic API; keeps the toolbar badge up to date.
import {
  LimitsError,
  ThresholdNotifier,
  fetchApiCost,
  fetchClaudeWebOrgs,
  fetchClaudeWebUsage,
  headlinePercent,
  pickOrg,
  probeRateLimits,
  snapshotLevel,
} from '../../core/src/browser';
import type { Snapshot } from '../../core/src/model';
import { ExtSettings, ExtState, getSettings, getState, saveState } from './store';

type Section = 'subscription' | 'rateLimits' | 'cost';
const ALARM = 'claude-limits-tick';
const MIN_GAP_MS: Record<Section, number> = { subscription: 60e3, rateLimits: 120e3, cost: 120e3 };

const BADGE_COLORS = { ok: '#4a8a5c', warn: '#c27c1a', crit: '#c2412d', unknown: '#78746c' } as const;

let inFlight: Promise<Snapshot> | undefined;

function intervalMs(section: Section, s: ExtSettings): number {
  const min =
    section === 'subscription' ? s.subscriptionIntervalMin : section === 'rateLimits' ? s.rateLimitIntervalMin : s.costIntervalMin;
  return Math.max(MIN_GAP_MS[section], (Number(min) || 1) * 60e3);
}

function errText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

async function lastActiveOrg(): Promise<string | undefined> {
  try {
    const c = await chrome.cookies.get({ url: 'https://claude.ai', name: 'lastActiveOrg' });
    return c?.value || undefined;
  } catch {
    return undefined;
  }
}

export async function refresh(force = false): Promise<Snapshot> {
  if (inFlight) return inFlight;
  inFlight = (async () => {
    const settings = await getSettings();
    const state = await getState();
    const now = Date.now();
    const snap: Snapshot = { ...(state.snapshot ?? {}), updatedAt: new Date(now).toISOString() };
    state.lastRun ??= {};
    state.backoff ??= {};

    const due = (section: Section) => {
      if ((state.backoff![section] ?? 0) > now) return false;
      const last = state.lastRun![section] ?? 0;
      return force ? now - last > 15e3 : now - last >= intervalMs(section, settings) - 5e3;
    };
    const fail = (section: Section, e: unknown) => {
      if (e instanceof LimitsError && e.kind === 'rate_limited') state.backoff![section] = now + (e.retryAfterSec ?? 300) * 1000;
      return errText(e);
    };

    const jobs: Promise<void>[] = [];
    if (due('subscription')) {
      state.lastRun.subscription = now;
      jobs.push(
        (async () => {
          try {
            const orgs = await fetchClaudeWebOrgs();
            const org = pickOrg(orgs, await lastActiveOrg());
            if (!org) throw new LimitsError('no_credentials', 'claude.ai hesabı bulunamadı; tarayıcıda claude.ai’ye giriş yapın');
            snap.subscription = await fetchClaudeWebUsage(org.uuid);
            delete snap.subscriptionError;
          } catch (e) {
            snap.subscriptionError = fail('subscription', e);
          }
        })(),
      );
    }
    if (!settings.apiKey) {
      delete snap.rateLimits;
      delete snap.rateLimitsError;
    } else if (due('rateLimits')) {
      state.lastRun.rateLimits = now;
      jobs.push(
        (async () => {
          try {
            snap.rateLimits = await probeRateLimits(settings.apiKey, settings.probeModel || 'claude-haiku-5-5', { browser: true });
            delete snap.rateLimitsError;
          } catch (e) {
            snap.rateLimitsError = fail('rateLimits', e);
          }
        })(),
      );
    }
    if (!settings.adminKey) {
      delete snap.cost;
      delete snap.costError;
    } else if (due('cost')) {
      state.lastRun.cost = now;
      jobs.push(
        (async () => {
          try {
            snap.cost = await fetchApiCost(settings.adminKey, { browser: true, budgetUsd: settings.monthlyBudgetUsd || undefined });
            delete snap.costError;
          } catch (e) {
            snap.costError = fail('cost', e);
          }
        })(),
      );
    }
    await Promise.all(jobs);

    // Budget is local: re-apply so a settings change shows immediately.
    if (snap.cost) {
      if (settings.monthlyBudgetUsd > 0) {
        snap.cost.budgetUsd = settings.monthlyBudgetUsd;
        snap.cost.percentOfBudget = (snap.cost.monthToDateUsd / settings.monthlyBudgetUsd) * 100;
      } else {
        delete snap.cost.budgetUsd;
        delete snap.cost.percentOfBudget;
      }
    }

    state.snapshot = snap;
    await updateBadge(snap, settings);
    await maybeNotify(snap, settings, state);
    await saveState(state);
    return snap;
  })();
  try {
    return await inFlight;
  } finally {
    inFlight = undefined;
  }
}

async function updateBadge(s: Snapshot, settings: ExtSettings): Promise<void> {
  const t = { warn: Math.min(settings.warn, settings.crit), crit: Math.max(settings.warn, settings.crit) };
  const pct = headlinePercent(s, settings.displayMode);
  const level = snapshotLevel(s, t);
  const onlyErrors = !s.subscription && !s.rateLimits && !s.cost && !!(s.subscriptionError || s.rateLimitsError || s.costError);
  const text = onlyErrors ? '!' : pct === undefined ? '' : String(Math.min(100, Math.round(pct)));
  await chrome.action.setBadgeText({ text });
  await chrome.action.setBadgeBackgroundColor({ color: onlyErrors ? BADGE_COLORS.unknown : BADGE_COLORS[level] });
  try {
    await chrome.action.setBadgeTextColor({ color: '#ffffff' });
  } catch {
    /* older Chrome */
  }
  const lines = ['Claude Limits'];
  for (const w of s.subscription?.windows ?? []) lines.push(`${w.label}: %${Math.round(w.percent)}`);
  if (s.subscriptionError) lines.push(s.subscriptionError);
  await chrome.action.setTitle({ title: lines.join('\n') });
}

async function maybeNotify(s: Snapshot, settings: ExtSettings, state: ExtState): Promise<void> {
  const notifier = new ThresholdNotifier(state.notifyState ?? {});
  const t = { warn: Math.min(settings.warn, settings.crit), crit: Math.max(settings.warn, settings.crit) };
  const alerts = notifier.check(s, t);
  state.notifyState = notifier.state;
  if (!settings.notifications) return;
  for (const a of alerts) {
    try {
      await chrome.notifications.create(`claude-limits:${a.key}`, {
        type: 'basic',
        iconUrl: 'icons/icon128.png',
        title: 'Claude Limits',
        message: a.message,
        priority: a.level === 'crit' ? 2 : 0,
      });
    } catch {
      /* notifications blocked */
    }
  }
}

function ensureAlarm(): void {
  void chrome.alarms.get(ALARM).then((a) => {
    if (!a) void chrome.alarms.create(ALARM, { periodInMinutes: 1, delayInMinutes: 0.1 });
  });
}

chrome.runtime.onInstalled.addListener(() => {
  ensureAlarm();
  void refresh(true);
});
chrome.runtime.onStartup.addListener(() => {
  ensureAlarm();
  void refresh(false);
});
chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === ALARM) void refresh(false);
});
chrome.notifications.onClicked.addListener((id) => {
  if (id.startsWith('claude-limits:')) void chrome.tabs.create({ url: 'https://claude.ai/settings/usage' });
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.settings) void refresh(true);
});
chrome.runtime.onMessage.addListener((msg: { type?: string; force?: boolean }, _sender, sendResponse) => {
  if (msg?.type === 'refresh') {
    refresh(!!msg.force).then(
      (s) => sendResponse({ ok: true, snapshot: s }),
      (e) => sendResponse({ ok: false, error: errText(e) }),
    );
    return true; // async response
  }
  return false;
});
ensureAlarm();
