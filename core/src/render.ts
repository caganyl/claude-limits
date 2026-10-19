// Shared HTML renderer for the detail views (VS Code webview, tray popup, browser popup).
// Browser-safe. Hosts supply colours through the --cl-* CSS variables and wire clicks on
// elements carrying data-action="…" (refresh | settings | set-api-key | set-admin-key | open-usage).
import { formatCount, formatPercent, formatResetAt, formatResetIn, formatUsd, levelOf } from './format';
import type { Level, Snapshot, Thresholds } from './model';
import { DEFAULT_THRESHOLDS } from './model';

export interface RenderOptions {
  thresholds?: Thresholds;
  now?: Date;
  /** Whether the host has an API key / admin key configured (drives the empty-state hints). */
  hasApiKey?: boolean;
  hasAdminKey?: boolean;
  /** Show the action buttons in the footer. */
  actions?: boolean;
  /** Extra footer buttons, e.g. [{action:'open-usage', label:'claude.ai'}]. */
  extraActions?: Array<{ action: string; label: string }>;
  /** Hide the API section entirely (e.g. when the host can't use API keys). */
  hideApi?: boolean;
  /** Text for the subscription source chip; defaults from the snapshot. */
  sourceLabel?: string;
}

const esc = (s: unknown): string =>
  String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);

const PLAN_NAMES: Record<string, string> = { pro: 'Pro', max: 'Max', team: 'Team', enterprise: 'Enterprise', free: 'Free' };

function bar(percent: number, level: Level): string {
  const w = Math.max(0, Math.min(100, percent));
  return `<div class="cl-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(w)}"><div class="cl-fill cl-${level}" style="width:${w.toFixed(1)}%"></div></div>`;
}

function resetLine(resetsAt: string | undefined, now: Date): string {
  if (!resetsAt) return '';
  const inText = formatResetIn(resetsAt, now);
  const at = formatResetAt(resetsAt, now);
  if (!inText) return '';
  return inText === 'şimdi' ? 'Sıfırlanıyor' : `${inText} sonra sıfırlanır${at ? ` · ${at}` : ''}`;
}

function sinceText(iso: string | undefined, now: Date): string {
  if (!iso) return '';
  const secs = Math.max(0, Math.round((now.getTime() - Date.parse(iso)) / 1000));
  if (!Number.isFinite(secs)) return '';
  if (secs < 60) return 'az önce';
  if (secs < 3600) return `${Math.round(secs / 60)} dk önce`;
  return `${Math.round(secs / 3600)} sa önce`;
}

export function renderSnapshotHtml(s: Snapshot, o: RenderOptions = {}): string {
  const t = o.thresholds ?? DEFAULT_THRESHOLDS;
  const now = o.now ?? new Date();
  const parts: string[] = ['<div class="cl">'];

  // ------------------------------------------------ subscription
  const sub = s.subscription;
  const chipBits: string[] = [];
  if (sub?.plan) chipBits.push(PLAN_NAMES[sub.plan.toLowerCase()] ?? sub.plan);
  chipBits.push(o.sourceLabel ?? (sub?.source === 'claude-web' ? 'claude.ai' : 'Claude Code'));
  parts.push(
    `<section class="cl-sec"><header class="cl-head"><h2>Abonelik limitleri</h2>${sub ? `<span class="cl-chip">${esc(chipBits.join(' · '))}</span>` : ''}</header>`,
  );
  if (sub && sub.windows.length) {
    for (const w of sub.windows) {
      const lv = levelOf(w.percent, t);
      parts.push(
        `<div class="cl-row${w.active ? ' cl-active' : ''}">` +
          `<div class="cl-top"><span class="cl-label">${esc(w.label)}${w.active ? ' <span class="cl-tag" title="Şu anda hesabı sınırlayan limit">bağlayıcı</span>' : ''}</span>` +
          `<span class="cl-pct cl-t-${lv}">${formatPercent(w.percent)}</span></div>` +
          bar(w.percent, lv) +
          `<div class="cl-sub">${esc(resetLine(w.resetsAt, now))}</div></div>`,
      );
    }
    if (sub.extra?.enabled) {
      const pct = sub.extra.percent;
      const lim = sub.extra.monthlyLimitUsd;
      const used = pct !== undefined && lim !== undefined ? (pct / 100) * lim : undefined;
      parts.push(
        `<div class="cl-row"><div class="cl-top"><span class="cl-label">Ek kullanım (aylık)</span>` +
          `<span class="cl-pct cl-t-${levelOf(pct, t)}">${pct !== undefined ? formatPercent(pct) : 'açık'}</span></div>` +
          (pct !== undefined ? bar(pct, levelOf(pct, t)) : '') +
          `<div class="cl-sub">${lim !== undefined ? `${used !== undefined ? `${formatUsd(used)} / ` : 'Limit '}${formatUsd(lim)}` : ''}</div></div>`,
      );
    }
  } else if (!s.subscriptionError) {
    parts.push('<p class="cl-empty">Veri bekleniyor…</p>');
  }
  if (s.subscriptionError) parts.push(`<p class="cl-err">${esc(s.subscriptionError)}</p>`);
  parts.push('</section>');

  // ------------------------------------------------ API
  if (!o.hideApi) {
    parts.push('<section class="cl-sec"><header class="cl-head"><h2>API limitleri</h2>');
    if (s.rateLimits) parts.push(`<span class="cl-chip">${esc(s.rateLimits.model)}</span>`);
    parts.push('</header>');

    if (s.rateLimits) {
      if (s.rateLimits.limited) parts.push('<p class="cl-err">HTTP 429 — dakikalık limit şu an dolu.</p>');
      for (const b of s.rateLimits.buckets) {
        const lv = levelOf(b.percentUsed, t);
        parts.push(
          `<div class="cl-row"><div class="cl-top"><span class="cl-label">${esc(b.label)}</span>` +
            `<span class="cl-pct cl-t-${lv}">${formatCount(b.remaining)} / ${formatCount(b.limit)} kaldı</span></div>` +
            bar(b.percentUsed, lv) +
            `<div class="cl-sub">%${Math.round(b.percentUsed)} kullanıldı · ölçüm ${esc(sinceText(s.rateLimits.fetchedAt, now))}</div></div>`,
        );
      }
    } else if (!s.rateLimitsError) {
      parts.push(
        `<p class="cl-empty">${o.hasApiKey ? 'Ölçülüyor…' : 'Dakikalık rate limit için bir API anahtarı (sk-ant-api…) girin.'}` +
          `${!o.hasApiKey && o.actions ? ' <button class="cl-link" data-action="set-api-key">API anahtarı gir</button>' : ''}</p>`,
      );
    }
    if (s.rateLimitsError) parts.push(`<p class="cl-err">${esc(s.rateLimitsError)}</p>`);

    if (s.cost) {
      const c = s.cost;
      const tiles = [
        `<div class="cl-tile"><span>Bu ay</span><strong>${formatUsd(c.monthToDateUsd)}</strong></div>`,
        `<div class="cl-tile"><span>Bugün (UTC)</span><strong>${formatUsd(c.todayUsd)}</strong></div>`,
      ];
      if (c.tokensToday) {
        const k = c.tokensToday;
        tiles.push(
          `<div class="cl-tile"><span>Bugün token</span><strong>${formatCount(k.uncachedInput + k.cacheRead + k.cacheWrite)}</strong><em>girdi · ${formatCount(k.output)} çıktı</em></div>`,
        );
      }
      parts.push(`<div class="cl-tiles">${tiles.join('')}</div>`);
      if (c.budgetUsd !== undefined && c.percentOfBudget !== undefined) {
        const lv = levelOf(c.percentOfBudget, t);
        parts.push(
          `<div class="cl-row"><div class="cl-top"><span class="cl-label">Aylık bütçe</span>` +
            `<span class="cl-pct cl-t-${lv}">${formatPercent(c.percentOfBudget)}</span></div>` +
            bar(c.percentOfBudget, lv) +
            `<div class="cl-sub">${formatUsd(c.monthToDateUsd)} / ${formatUsd(c.budgetUsd)}</div></div>`,
        );
      }
    } else if (!s.costError) {
      parts.push(
        `<p class="cl-empty">${o.hasAdminKey ? 'Harcama yükleniyor…' : 'Harcama ve token kullanımı için Admin API anahtarı (sk-ant-admin…) girin.'}` +
          `${!o.hasAdminKey && o.actions ? ' <button class="cl-link" data-action="set-admin-key">Admin anahtarı gir</button>' : ''}</p>`,
      );
    }
    if (s.costError) parts.push(`<p class="cl-err">${esc(s.costError)}</p>`);
    parts.push('</section>');
  }

  // ------------------------------------------------ footer
  const updated = Date.parse(s.updatedAt) > 0 ? new Date(s.updatedAt).toLocaleTimeString('tr-TR') : '–';
  parts.push(`<footer class="cl-foot"><span>Son güncelleme ${esc(updated)}</span>`);
  if (o.actions) {
    parts.push('<span class="cl-actions">');
    for (const a of o.extraActions ?? []) parts.push(`<button data-action="${esc(a.action)}">${esc(a.label)}</button>`);
    parts.push('<button data-action="settings">Ayarlar</button><button class="cl-primary" data-action="refresh">Yenile</button></span>');
  }
  parts.push('</footer></div>');
  return parts.join('');
}

/**
 * Base stylesheet. Hosts define these variables (defaults are a neutral dark/light pair):
 * --cl-fg --cl-muted --cl-bg --cl-card --cl-border --cl-track --cl-ok --cl-warn --cl-crit --cl-accent --cl-font
 */
export const BASE_CSS = `
.cl{font-family:var(--cl-font,system-ui,-apple-system,"Segoe UI",sans-serif);color:var(--cl-fg);font-size:13px;line-height:1.4;display:flex;flex-direction:column;gap:12px}
.cl-sec{background:var(--cl-card);border:1px solid var(--cl-border);border-radius:10px;padding:12px 14px;display:flex;flex-direction:column;gap:10px}
.cl-head{display:flex;align-items:center;justify-content:space-between;gap:8px}
.cl-head h2{margin:0;font-size:11px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:var(--cl-muted)}
.cl-chip{font-size:11px;color:var(--cl-muted);border:1px solid var(--cl-border);border-radius:999px;padding:1px 8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:60%}
.cl-row{display:flex;flex-direction:column;gap:4px}
.cl-top{display:flex;justify-content:space-between;align-items:baseline;gap:8px}
.cl-label{font-weight:500}
.cl-tag{font-size:10px;font-weight:600;text-transform:uppercase;letter-spacing:.04em;color:var(--cl-accent);margin-left:4px}
.cl-pct{font-variant-numeric:tabular-nums;font-weight:600;white-space:nowrap}
.cl-t-warn{color:var(--cl-warn)}.cl-t-crit{color:var(--cl-crit)}
.cl-bar{height:6px;border-radius:999px;background:var(--cl-track);overflow:hidden}
.cl-fill{height:100%;border-radius:999px;background:var(--cl-ok);transition:width .4s ease}
.cl-fill.cl-warn{background:var(--cl-warn)}.cl-fill.cl-crit{background:var(--cl-crit)}
.cl-sub{font-size:11.5px;color:var(--cl-muted);min-height:1em}
.cl-active .cl-bar{outline:1px solid var(--cl-accent);outline-offset:1px}
.cl-err{margin:0;font-size:12px;color:var(--cl-crit)}
.cl-empty{margin:0;font-size:12px;color:var(--cl-muted)}
.cl-tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(88px,1fr));gap:8px}
.cl-tile{border:1px solid var(--cl-border);border-radius:8px;padding:8px 10px;display:flex;flex-direction:column;gap:2px}
.cl-tile span{font-size:11px;color:var(--cl-muted)}.cl-tile strong{font-size:15px;font-variant-numeric:tabular-nums}
.cl-tile em{font-style:normal;font-size:10.5px;color:var(--cl-muted)}
.cl-foot{display:flex;justify-content:space-between;align-items:center;gap:8px;font-size:11.5px;color:var(--cl-muted);flex-wrap:wrap;position:sticky;bottom:0;padding:8px 0 2px;background:var(--cl-bg,transparent)}
.cl-actions{display:flex;gap:6px}
.cl button{font:inherit;font-size:12px;color:var(--cl-fg);background:transparent;border:1px solid var(--cl-border);border-radius:6px;padding:3px 10px;cursor:pointer}
.cl button:hover{border-color:var(--cl-accent)}
.cl button.cl-primary{background:var(--cl-accent);border-color:var(--cl-accent);color:var(--cl-on-accent,#fff)}
.cl button.cl-link{border:none;padding:0;color:var(--cl-accent);text-decoration:underline;font-size:12px}
`;

/** Default palette for hosts without their own theme (tray popup, browser popup). */
export const DEFAULT_THEME_CSS = `
:root{--cl-fg:#1f1e1c;--cl-muted:#6b6862;--cl-bg:#f7f6f3;--cl-card:#ffffff;--cl-border:#e4e1da;--cl-track:#ebe8e1;
--cl-ok:#4a8a5c;--cl-warn:#c27c1a;--cl-crit:#c2412d;--cl-accent:#c96442;--cl-on-accent:#fff}
@media (prefers-color-scheme:dark){:root{--cl-fg:#ecebe8;--cl-muted:#a19d95;--cl-bg:#1c1b19;--cl-card:#262522;--cl-border:#3a3834;
--cl-track:#36342f;--cl-ok:#6fae7f;--cl-warn:#e0a24a;--cl-crit:#e5705c;--cl-accent:#d97757;--cl-on-accent:#1c1b19}}
`;
