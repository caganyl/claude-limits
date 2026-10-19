// Decides when to raise a "limit almost reached" notification. Browser-safe.
// A window notifies once per level (warn, then crit) per reset period.
import { formatPercent, formatResetIn, levelOf } from './format';
import type { Level, Snapshot, Thresholds } from './model';

export interface LimitAlert {
  key: string;
  level: Exclude<Level, 'ok' | 'unknown'>;
  message: string;
}

const RANK: Record<Level, number> = { unknown: 0, ok: 0, warn: 1, crit: 2 };

export class ThresholdNotifier {
  /** key → highest level already announced. Serializable for hosts that persist it. */
  constructor(public state: Record<string, Level> = {}) {}

  check(s: Snapshot, t: Thresholds, now: Date = new Date()): LimitAlert[] {
    const alerts: LimitAlert[] = [];
    const seen = new Set<string>();
    const consider = (key: string, percent: number, text: (pct: string) => string) => {
      seen.add(key);
      const lv = levelOf(percent, t);
      const prev = this.state[key] ?? 'ok';
      if (RANK[lv] > RANK[prev] && (lv === 'warn' || lv === 'crit')) {
        alerts.push({ key, level: lv, message: text(formatPercent(percent)) });
      }
      // Track drops too, so a later climb notifies again only after a reset.
      if (RANK[lv] > RANK[prev] || lv === 'ok') this.state[key] = lv;
    };

    for (const w of s.subscription?.windows ?? []) {
      if (w.undocumented) continue;
      const reset = formatResetIn(w.resetsAt, now);
      consider(`sub:${w.id}:${w.resetsAt ?? ''}`, w.percent, (p) =>
        `Claude ${w.label} limiti ${p}${reset && reset !== 'şimdi' ? ` — ${reset} sonra sıfırlanır` : ''}.`,
      );
    }
    for (const b of s.rateLimits?.buckets ?? []) {
      consider(`rl:${b.id}`, b.percentUsed, (p) => `Claude API ${b.label} kullanımı ${p} (${s.rateLimits!.model}).`);
    }
    if (s.cost?.percentOfBudget !== undefined) {
      const month = s.cost.fetchedAt.slice(0, 7);
      consider(`cost:${month}`, s.cost.percentOfBudget, (p) => `Claude API aylık bütçenin ${p} kadarı harcandı.`);
    }
    // Forget windows that have rolled over (their key contains the old reset time).
    for (const k of Object.keys(this.state)) if (!seen.has(k)) delete this.state[k];
    return alerts;
  }
}
