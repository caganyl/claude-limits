import { snapshotLevel } from '../../core/src/format';
import { BASE_CSS, DEFAULT_THEME_CSS, renderSnapshotHtml } from '../../core/src/render';
import type { PublicSettings, SettingsUpdate, SnapshotMessage, TrayBridge } from './shared';

declare global {
  interface Window {
    claudeLimits: TrayBridge;
  }
}

const api = window.claudeLimits;
const $ = <T extends HTMLElement>(sel: string) => document.querySelector(sel) as T;

const style = document.createElement('style');
style.textContent = `${DEFAULT_THEME_CSS}${BASE_CSS}
*{box-sizing:border-box}
html,body{margin:0;background:var(--cl-bg);color:var(--cl-fg);font-family:system-ui,-apple-system,"Segoe UI",sans-serif;user-select:none}
html{overflow-y:auto;scrollbar-width:thin}body{overflow:visible}
.top{position:sticky;top:0;z-index:2;background:var(--cl-bg);margin:-10px -12px 0;padding:10px 12px 6px}
#app{padding:10px 12px 12px;display:flex;flex-direction:column;gap:10px}
.top{display:flex;align-items:center;justify-content:space-between}
.brand{display:flex;align-items:center;gap:8px;font-weight:600;font-size:13px;letter-spacing:.01em}
.dot{width:8px;height:8px;border-radius:50%;background:var(--cl-muted)}
.dot.ok{background:var(--cl-ok)}.dot.warn{background:var(--cl-warn)}.dot.crit{background:var(--cl-crit)}
.tools{display:flex;gap:2px}
.icon{display:grid;place-items:center;width:26px;height:26px;border:none;border-radius:6px;background:transparent;color:var(--cl-muted);cursor:pointer}
.icon:hover{background:var(--cl-track);color:var(--cl-fg)}
form.cl label{display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--cl-muted)}
form.cl label.check{flex-direction:row;align-items:center;gap:8px;color:var(--cl-fg)}
form.cl input[type=text],form.cl input[type=password],form.cl input[type=number],form.cl select{font:inherit;font-size:12.5px;color:var(--cl-fg);
 background:var(--cl-bg);border:1px solid var(--cl-border);border-radius:6px;padding:5px 8px;outline:none;user-select:text}
form.cl input:focus,form.cl select:focus{border-color:var(--cl-accent)}
form.cl input,form.cl select{min-width:0;width:100%}form.cl input[type=checkbox]{width:auto}
form.cl input.saved::placeholder{color:var(--cl-ok)}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:8px}.grid3{display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px}
.hint{margin:0;font-size:11.5px;color:var(--cl-muted)}.hint code{font-size:11px}
.link-danger{border:none!important;padding:0!important;color:var(--cl-crit)!important;font-size:12px}
[hidden]{display:none!important}
`;
document.head.appendChild(style);

let last: SnapshotMessage | undefined;
let settingsOpen = false;

function reportSize(): void {
  requestAnimationFrame(() => api.resize($('#app').getBoundingClientRect().height));
}

function renderMain(): void {
  if (!last) return;
  $('#root').innerHTML = renderSnapshotHtml(last.snapshot, {
    thresholds: last.thresholds,
    hasApiKey: last.hasApiKey,
    hasAdminKey: last.hasAdminKey,
    actions: true,
    extraActions: [{ action: 'open-usage', label: 'claude.ai' }],
  });
  const level = snapshotLevel(last.snapshot, last.thresholds);
  $('#dot').className = `dot ${level}`;
  if (!settingsOpen) reportSize();
}

api.onSnapshot((m) => {
  last = m;
  renderMain();
});

$('#root').addEventListener('click', (e) => {
  const el = (e.target as HTMLElement).closest('[data-action]');
  if (!el) return;
  const action = el.getAttribute('data-action');
  if (action === 'refresh') {
    el.textContent = 'Yenileniyor…';
    void api.refresh();
  } else if (action === 'open-usage') void api.openUsagePage();
  else if (action === 'settings') void openSettings();
  else if (action === 'set-api-key') void openSettings('apiKey');
  else if (action === 'set-admin-key') void openSettings('adminKey');
});

// ------------------------------------------------------------------ settings view
const form = $<HTMLFormElement>('#settings');
const SECRET_FIELDS = ['apiKey', 'adminKey', 'oauthToken'] as const;

async function openSettings(focus?: string): Promise<void> {
  const s = await api.getSettings();
  fillForm(s);
  settingsOpen = true;
  $('#view-main').hidden = true;
  $('#view-settings').hidden = false;
  $('#title').textContent = 'Ayarlar';
  reportSize();
  if (focus) (form.elements.namedItem(focus) as HTMLInputElement | null)?.focus();
}

function closeSettings(): void {
  settingsOpen = false;
  $('#view-settings').hidden = true;
  $('#view-main').hidden = false;
  $('#title').textContent = 'Claude Limits';
  renderMain();
  reportSize();
}

function fillForm(s: PublicSettings): void {
  const set = (name: string, v: unknown) => {
    const el = form.elements.namedItem(name) as HTMLInputElement | HTMLSelectElement | null;
    if (!el) return;
    if (el instanceof HTMLInputElement && el.type === 'checkbox') el.checked = !!v;
    else el.value = String(v ?? '');
  };
  for (const k of [
    'subscriptionEnabled', 'displayMode', 'warn', 'crit', 'subscriptionIntervalSec', 'rateLimitIntervalSec',
    'costIntervalSec', 'probeModel', 'monthlyBudgetUsd', 'notifications', 'launchAtLogin', 'menuBarText',
  ] as const) set(k, s[k]);
  const saved: Record<(typeof SECRET_FIELDS)[number], boolean> = {
    apiKey: s.hasApiKey,
    adminKey: s.hasAdminKey,
    oauthToken: s.hasOAuthToken,
  };
  for (const k of SECRET_FIELDS) {
    const el = form.elements.namedItem(k) as HTMLInputElement;
    el.value = '';
    el.classList.toggle('saved', saved[k]);
    el.dataset.saved = saved[k] ? '1' : '';
    if (el.dataset.ph === undefined) el.dataset.ph = el.placeholder;
    el.placeholder = saved[k] ? 'Kayıtlı ✓ (değiştirmek için yazın, silmek için "-")' : el.dataset.ph;
  }
  $('#secure-hint').textContent = s.secureStorage
    ? `Anahtarlar ${s.platform === 'darwin' ? 'macOS Anahtar Zinciri' : s.platform === 'win32' ? 'Windows DPAPI' : 'sistem anahtarlığı'} ile şifrelenerek saklanır.`
    : 'Uyarı: bu sistemde şifreli depolama yok; anahtarlar yalnızca kodlanmış olarak saklanır.';
  document.querySelectorAll<HTMLElement>('.mac-only').forEach((el) => (el.hidden = s.platform !== 'darwin'));
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const v = (name: string) => form.elements.namedItem(name) as HTMLInputElement;
  const u: SettingsUpdate = {
    subscriptionEnabled: v('subscriptionEnabled').checked,
    displayMode: (form.elements.namedItem('displayMode') as HTMLSelectElement).value as SettingsUpdate['displayMode'],
    warn: Number(v('warn').value),
    crit: Number(v('crit').value),
    subscriptionIntervalSec: Number(v('subscriptionIntervalSec').value),
    rateLimitIntervalSec: Number(v('rateLimitIntervalSec').value),
    costIntervalSec: Number(v('costIntervalSec').value),
    probeModel: v('probeModel').value,
    monthlyBudgetUsd: Number(v('monthlyBudgetUsd').value),
    notifications: v('notifications').checked,
    launchAtLogin: v('launchAtLogin').checked,
    menuBarText: v('menuBarText').checked,
  };
  for (const k of SECRET_FIELDS) {
    const raw = v(k).value.trim();
    if (raw === '-') u[k] = '';
    else if (raw) u[k] = raw;
  }
  if (u.apiKey?.startsWith('sk-ant-admin')) {
    v('apiKey').setCustomValidity('Bu bir Admin anahtarı; aşağıdaki alana girin.');
    v('apiKey').reportValidity();
    v('apiKey').setCustomValidity('');
    return;
  }
  if (u.adminKey && !u.adminKey.startsWith('sk-ant-admin')) {
    v('adminKey').setCustomValidity('Admin anahtarları "sk-ant-admin" ile başlar.');
    v('adminKey').reportValidity();
    v('adminKey').setCustomValidity('');
    return;
  }
  await api.saveSettings(u);
  closeSettings();
});

$('#btn-cancel').addEventListener('click', () => closeSettings());
$('#btn-settings').addEventListener('click', () => (settingsOpen ? closeSettings() : void openSettings()));
$('#btn-close').addEventListener('click', () => api.hide());
$('#btn-quit').addEventListener('click', () => api.quit());
api.onShowSettings(() => void openSettings());
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') settingsOpen ? closeSettings() : api.hide();
});
reportSize();
