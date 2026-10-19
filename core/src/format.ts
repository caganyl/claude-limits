// Formatting helpers shared by every UI. Browser-safe.
import type { Level, LimitWindow, Snapshot, Thresholds } from './model';
import { DEFAULT_THRESHOLDS } from './model';

export function levelOf(percent: number | undefined, t: Thresholds = DEFAULT_THRESHOLDS): Level {
  if (percent === undefined || !Number.isFinite(percent)) return 'unknown';
  if (percent >= t.crit) return 'crit';
  if (percent >= t.warn) return 'warn';
  return 'ok';
}

const ORDER: Record<Level, number> = { unknown: -1, ok: 0, warn: 1, crit: 2 };

export function worstLevel(levels: Level[]): Level {
  return levels.reduce<Level>((acc, l) => (ORDER[l] > ORDER[acc] ? l : acc), 'unknown');
}

export function formatPercent(p: number | undefined): string {
  if (p === undefined || !Number.isFinite(p)) return '–';
  return `${Math.round(p)}%`;
}

/** "2 sa 15 dk", "3 gün 4 sa", "45 dk", "şimdi". */
export function formatResetIn(resetsAt: string | undefined, now: Date = new Date()): string {
  if (!resetsAt) return '';
  const t = Date.parse(resetsAt);
  if (!Number.isFinite(t)) return '';
  const secs = Math.round((t - now.getTime()) / 1000);
  if (secs <= 0) return 'şimdi';
  if (secs < 60) return `${secs} sn`;
  let mins = Math.round(secs / 60);
  const days = Math.floor(mins / 1440);
  mins -= days * 1440;
  const hours = Math.floor(mins / 60);
  mins -= hours * 60;
  if (days > 0) return hours > 0 ? `${days} gün ${hours} sa` : `${days} gün`;
  if (hours > 0) return mins > 0 ? `${hours} sa ${mins} dk` : `${hours} sa`;
  return `${mins} dk`;
}

/** Local wall-clock reset time: "Pzt 18:00" or "18:00" when today. */
export function formatResetAt(resetsAt: string | undefined, now: Date = new Date(), locale = 'tr-TR'): string {
  if (!resetsAt) return '';
  const d = new Date(resetsAt);
  if (Number.isNaN(d.getTime())) return '';
  const time = d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' });
  const sameDay = d.toDateString() === now.toDateString();
  if (sameDay) return time;
  const day = d.toLocaleDateString(locale, { weekday: 'short' });
  return `${day} ${time}`;
}

export function formatUsd(v: number | undefined): string {
  if (v === undefined || !Number.isFinite(v)) return '–';
  return `$${v.toFixed(2)}`;
}

export function formatCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(n >= 10_000 ? 0 : 1)}K`;
  return String(Math.round(n));
}

/** Plain-text progress bar: "███████░░░". */
export function textBar(percent: number, width = 10): string {
  const filled = Math.max(0, Math.min(width, Math.round((percent / 100) * width)));
  return '█'.repeat(filled) + '░'.repeat(width - filled);
}

export type DisplayMode = 'session' | 'weekly' | 'both' | 'highest';

/** Windows to show in a compact status line for the given mode. */
export function pickCompactWindows(all: LimitWindow[], mode: DisplayMode): LimitWindow[] {
  const windows = all.filter((w) => !w.undocumented);
  const session = windows.find((w) => w.id === 'session');
  const weekly = windows.find((w) => w.id === 'weekly_all');
  const scopedMax = windows
    .filter((w) => w.id.startsWith('weekly_scoped:'))
    .sort((a, b) => b.percent - a.percent)[0];
  // A per-model weekly cap that is binding or higher than the all-model one is the more useful number.
  const weeklyShown = scopedMax && (scopedMax.active || !weekly || scopedMax.percent > weekly.percent) ? scopedMax : weekly;
  switch (mode) {
    case 'session':
      return session ? [session] : windows.slice(0, 1);
    case 'weekly':
      return weeklyShown ? [weeklyShown] : windows.slice(0, 1);
    case 'highest': {
      const top = [...windows].sort((a, b) => b.percent - a.percent)[0];
      return top ? [top] : [];
    }
    default:
      return [session, weeklyShown].filter((w): w is LimitWindow => !!w);
  }
}

/** Compact one-liner such as "5s 42% · 7g 18%" used by status bars and the macOS menu bar. */
export function compactText(snapshot: Snapshot, mode: DisplayMode = 'both'): string {
  const parts: string[] = [];
  if (snapshot.subscription) {
    for (const w of pickCompactWindows(snapshot.subscription.windows, mode)) {
      parts.push(`${w.shortLabel} ${formatPercent(w.percent)}`);
    }
  }
  if (parts.length === 0 && snapshot.rateLimits) {
    const worst = [...snapshot.rateLimits.buckets].sort((a, b) => b.percentUsed - a.percentUsed)[0];
    if (worst) parts.push(`API ${formatPercent(worst.percentUsed)}`);
  }
  if (parts.length === 0 && snapshot.cost) parts.push(`API ${formatUsd(snapshot.cost.monthToDateUsd)}`);
  return parts.join(' · ');
}

/** Highest-severity level across everything in the snapshot. */
export function snapshotLevel(snapshot: Snapshot, t: Thresholds = DEFAULT_THRESHOLDS): Level {
  const levels: Level[] = [];
  snapshot.subscription?.windows.forEach((w) => {
    if (!w.undocumented) levels.push(levelOf(w.percent, t));
  });
  snapshot.rateLimits?.buckets.forEach((b) => levels.push(levelOf(b.percentUsed, t)));
  if (snapshot.rateLimits?.limited) levels.push('crit');
  if (snapshot.cost?.percentOfBudget !== undefined) levels.push(levelOf(snapshot.cost.percentOfBudget, t));
  return worstLevel(levels);
}

/** The number to put on a small icon/badge: the most relevant subscription percent, else API. */
export function headlinePercent(snapshot: Snapshot, mode: DisplayMode = 'session'): number | undefined {
  const sub = snapshot.subscription;
  if (sub && sub.windows.length) {
    const picked = pickCompactWindows(sub.windows, mode === 'both' ? 'highest' : mode);
    if (picked[0]) return picked[0].percent;
  }
  if (snapshot.rateLimits?.buckets.length) {
    return Math.max(...snapshot.rateLimits.buckets.map((b) => b.percentUsed));
  }
  if (snapshot.cost?.percentOfBudget !== undefined) return snapshot.cost.percentOfBudget;
  return undefined;
}
