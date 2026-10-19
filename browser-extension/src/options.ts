import { BASE_CSS, DEFAULT_THEME_CSS } from '../../core/src/browser';
import { ExtSettings, getSettings, saveSettings } from './store';

const style = document.createElement('style');
style.textContent = `${DEFAULT_THEME_CSS}${BASE_CSS}
*{box-sizing:border-box}
html,body{margin:0;background:var(--cl-bg);color:var(--cl-fg);font-family:system-ui,-apple-system,"Segoe UI",sans-serif}
.wrap{max-width:560px;margin:0 auto;padding:32px 16px 48px}
h1{font-size:20px;margin:0 0 6px}.lead{margin:0 0 18px;color:var(--cl-muted);font-size:13.5px;line-height:1.5}
form.cl label{display:flex;flex-direction:column;gap:4px;font-size:12.5px;color:var(--cl-muted)}
form.cl label.check{flex-direction:row;align-items:center;gap:8px;color:var(--cl-fg)}
form.cl input[type=text],form.cl input[type=password],form.cl input[type=number],form.cl select{font:inherit;font-size:13px;color:var(--cl-fg);
 background:var(--cl-bg);border:1px solid var(--cl-border);border-radius:6px;padding:6px 9px;outline:none}
form.cl input:focus,form.cl select:focus{border-color:var(--cl-accent)}
form.cl input,form.cl select{min-width:0;width:100%}form.cl input[type=checkbox]{width:auto}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:10px}.grid3{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px}
.hint{margin:0;font-size:12px;color:var(--cl-muted);line-height:1.5}
#status{color:var(--cl-ok)}`;
document.head.appendChild(style);

const form = document.getElementById('f') as HTMLFormElement;
const field = (n: string) => form.elements.namedItem(n) as HTMLInputElement | HTMLSelectElement;
const NUMS = ['warn', 'crit', 'subscriptionIntervalMin', 'rateLimitIntervalMin', 'costIntervalMin', 'monthlyBudgetUsd'] as const;

async function load(): Promise<void> {
  const s = await getSettings();
  field('displayMode').value = s.displayMode === 'both' ? 'session' : s.displayMode;
  for (const k of NUMS) field(k).value = String(s[k]);
  field('probeModel').value = s.probeModel;
  field('apiKey').value = s.apiKey;
  field('adminKey').value = s.adminKey;
  (field('notifications') as HTMLInputElement).checked = s.notifications;
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const apiKey = field('apiKey').value.trim();
  const adminKey = field('adminKey').value.trim();
  const apiEl = field('apiKey') as HTMLInputElement;
  const adminEl = field('adminKey') as HTMLInputElement;
  apiEl.setCustomValidity(apiKey.startsWith('sk-ant-admin') ? 'Bu bir Admin anahtarı; aşağıdaki alana girin.' : '');
  adminEl.setCustomValidity(adminKey && !adminKey.startsWith('sk-ant-admin') ? 'Admin anahtarları "sk-ant-admin" ile başlar.' : '');
  if (!form.reportValidity()) return;
  const num = (k: (typeof NUMS)[number], min: number, max: number) => Math.min(max, Math.max(min, Number(field(k).value) || min));
  const next: Partial<ExtSettings> = {
    displayMode: field('displayMode').value as ExtSettings['displayMode'],
    warn: num('warn', 1, 100),
    crit: num('crit', 1, 100),
    subscriptionIntervalMin: num('subscriptionIntervalMin', 1, 1440),
    rateLimitIntervalMin: num('rateLimitIntervalMin', 2, 1440),
    costIntervalMin: num('costIntervalMin', 2, 1440),
    monthlyBudgetUsd: Math.max(0, Number(field('monthlyBudgetUsd').value) || 0),
    probeModel: field('probeModel').value.trim() || 'claude-haiku-5-5',
    notifications: (field('notifications') as HTMLInputElement).checked,
    apiKey,
    adminKey,
  };
  await saveSettings(next);
  const status = document.getElementById('status')!;
  status.textContent = 'Kaydedildi ✓';
  setTimeout(() => (status.textContent = ''), 2500);
});

void load();
