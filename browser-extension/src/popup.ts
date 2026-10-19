import { BASE_CSS, DEFAULT_THEME_CSS, renderSnapshotHtml } from '../../core/src/browser';
import type { Snapshot } from '../../core/src/model';
import { getSettings, getState } from './store';

const style = document.createElement('style');
style.textContent = `${DEFAULT_THEME_CSS}${BASE_CSS}
*{box-sizing:border-box}
html,body{margin:0;background:var(--cl-bg)}
body{width:360px;padding:12px;font-family:system-ui,-apple-system,"Segoe UI",sans-serif}`;
document.head.appendChild(style);

const root = document.getElementById('root')!;

async function draw(snapshot?: Snapshot): Promise<void> {
  const settings = await getSettings();
  const snap = snapshot ?? (await getState()).snapshot ?? { updatedAt: '' };
  root.innerHTML = renderSnapshotHtml(snap, {
    thresholds: { warn: Math.min(settings.warn, settings.crit), crit: Math.max(settings.warn, settings.crit) },
    hasApiKey: !!settings.apiKey,
    hasAdminKey: !!settings.adminKey,
    actions: true,
    extraActions: [{ action: 'open-usage', label: 'claude.ai' }],
  });
}

root.addEventListener('click', (e) => {
  const el = (e.target as HTMLElement).closest('[data-action]');
  if (!el) return;
  switch (el.getAttribute('data-action')) {
    case 'refresh':
      el.textContent = 'Yenileniyor…';
      void chrome.runtime.sendMessage({ type: 'refresh', force: true }).then((r: { snapshot?: Snapshot }) => draw(r?.snapshot));
      break;
    case 'open-usage':
      void chrome.tabs.create({ url: 'https://claude.ai/settings/usage' });
      break;
    case 'settings':
    case 'set-api-key':
    case 'set-admin-key':
      void chrome.runtime.openOptionsPage();
      break;
  }
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && (changes.state || changes.settings)) void draw();
});

void draw();
// Opening the popup is a good moment to catch up (respects the minimum intervals).
void chrome.runtime.sendMessage({ type: 'refresh', force: false });
