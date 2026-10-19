import * as vscode from 'vscode';
import {
  BASE_CSS,
  DEFAULT_CONFIG,
  DisplayMode,
  LimitsService,
  ServiceConfig,
  Snapshot,
  Thresholds,
  ThresholdNotifier,
  compactText,
  formatPercent,
  formatResetIn,
  formatUsd,
  renderSnapshotHtml,
  snapshotLevel,
} from '../../core/src/index';

const SECRET_API = 'claudeLimits.apiKey';
const SECRET_ADMIN = 'claudeLimits.adminKey';
const SECRET_OAUTH = 'claudeLimits.oauthToken';
const NOTIFY_STATE = 'claudeLimits.notifyState';

interface Secrets {
  apiKey?: string;
  adminKey?: string;
  oauthToken?: string;
}

let panel: vscode.WebviewPanel | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const secrets: Secrets = {};
  const loadSecrets = async () => {
    secrets.apiKey = (await context.secrets.get(SECRET_API)) || undefined;
    secrets.adminKey = (await context.secrets.get(SECRET_ADMIN)) || undefined;
    secrets.oauthToken = (await context.secrets.get(SECRET_OAUTH)) || undefined;
  };
  await loadSecrets();

  const cfg = () => vscode.workspace.getConfiguration('claudeLimits');
  const thresholds = (): Thresholds => {
    const warn = cfg().get<number>('thresholds.warn', 75);
    const crit = cfg().get<number>('thresholds.crit', 90);
    return { warn: Math.min(warn, crit), crit: Math.max(warn, crit) };
  };
  const serviceConfig = (): ServiceConfig => ({
    subscriptionEnabled: cfg().get<boolean>('subscription.enabled', true),
    manualOAuthToken: secrets.oauthToken,
    apiKey: secrets.apiKey,
    adminKey: secrets.adminKey,
    probeModel: cfg().get<string>('api.probeModel', DEFAULT_CONFIG.probeModel) || DEFAULT_CONFIG.probeModel,
    monthlyBudgetUsd: cfg().get<number>('api.monthlyBudgetUsd', 0) || undefined,
    subscriptionIntervalSec: cfg().get<number>('refresh.subscriptionSeconds', DEFAULT_CONFIG.subscriptionIntervalSec),
    rateLimitIntervalSec: cfg().get<number>('refresh.rateLimitSeconds', DEFAULT_CONFIG.rateLimitIntervalSec),
    costIntervalSec: cfg().get<number>('refresh.costSeconds', DEFAULT_CONFIG.costIntervalSec),
  });

  // ------------------------------------------------------------- status bar
  const item = vscode.window.createStatusBarItem('claudeLimits.status', vscode.StatusBarAlignment.Right, 100);
  item.name = 'Claude Limits';
  item.command = 'claudeLimits.showDetails';
  item.text = '$(loading~spin) Claude';
  item.tooltip = 'Claude limitleri yükleniyor…';
  item.show();
  context.subscriptions.push(item);

  const notifier = new ThresholdNotifier(context.globalState.get<Record<string, never>>(NOTIFY_STATE, {}));

  const render = (s: Snapshot) => {
    const t = thresholds();
    const mode = cfg().get<DisplayMode>('displayMode', 'both');
    const text = compactText(s, mode);
    const level = snapshotLevel(s, t);
    const hasError = !!(s.subscriptionError || s.rateLimitsError || s.costError);
    const icon = level === 'crit' ? '$(flame)' : level === 'warn' ? '$(warning)' : hasError && !text ? '$(error)' : '$(pulse)';
    item.text = `${icon} ${text || 'Claude'}`;
    item.backgroundColor =
      level === 'crit'
        ? new vscode.ThemeColor('statusBarItem.errorBackground')
        : level === 'warn'
          ? new vscode.ThemeColor('statusBarItem.warningBackground')
          : undefined;
    item.tooltip = buildTooltip(s);
    if (panel) void panel.webview.postMessage({ type: 'render', html: detailsHtml(s) });

    if (cfg().get<boolean>('notifications.enabled', true)) {
      for (const a of notifier.check(s, t)) {
        const show = a.level === 'crit' ? vscode.window.showErrorMessage : vscode.window.showWarningMessage;
        void show(a.message, 'Ayrıntılar').then((pick) => {
          if (pick) void vscode.commands.executeCommand('claudeLimits.showDetails');
        });
      }
      void context.globalState.update(NOTIFY_STATE, notifier.state);
    }
  };

  const detailsHtml = (s: Snapshot) =>
    renderSnapshotHtml(s, {
      thresholds: thresholds(),
      hasApiKey: !!secrets.apiKey,
      hasAdminKey: !!secrets.adminKey,
      actions: true,
    });

  const service = new LimitsService(serviceConfig, render);
  service.start(30);
  context.subscriptions.push({ dispose: () => service.stop() });

  // Re-render countdowns every minute even when no new data arrives.
  const minuteTimer = setInterval(() => render(service.snapshot), 60_000);
  context.subscriptions.push({ dispose: () => clearInterval(minuteTimer) });

  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('claudeLimits')) void service.refresh(true);
    }),
    context.secrets.onDidChange(async (e) => {
      if ([SECRET_API, SECRET_ADMIN, SECRET_OAUTH].includes(e.key)) {
        await loadSecrets();
        void service.refresh(true);
      }
    }),
  );

  // ------------------------------------------------------------- commands
  const askSecret = async (key: string, prompt: string, placeHolder: string, validate: (v: string) => string | undefined) => {
    const value = await vscode.window.showInputBox({
      prompt,
      placeHolder,
      password: true,
      ignoreFocusOut: true,
      validateInput: (v) => (v.trim() ? validate(v.trim()) : undefined),
    });
    if (value === undefined) return;
    if (value.trim()) {
      await context.secrets.store(key, value.trim());
      void vscode.window.showInformationMessage('Anahtar VS Code’un güvenli deposuna kaydedildi.');
    } else {
      await context.secrets.delete(key);
      void vscode.window.showInformationMessage('Anahtar silindi.');
    }
    await loadSecrets();
    await service.refresh(true);
  };

  context.subscriptions.push(
    vscode.commands.registerCommand('claudeLimits.refresh', async () => {
      item.text = `$(sync~spin) ${compactText(service.snapshot) || 'Claude'}`;
      render(await service.refresh(true));
    }),
    vscode.commands.registerCommand('claudeLimits.showDetails', () => showPanel(context, () => detailsHtml(service.snapshot), service)),
    vscode.commands.registerCommand('claudeLimits.setApiKey', () =>
      askSecret(SECRET_API, 'Anthropic API anahtarı (dakikalık rate limit için). Boş bırakıp Enter = sil.', 'sk-ant-api03-…', (v) =>
        v.startsWith('sk-ant-admin') ? 'Bu bir Admin anahtarı; "Admin API anahtarı gir" komutunu kullanın.' : undefined,
      ),
    ),
    vscode.commands.registerCommand('claudeLimits.setAdminKey', () =>
      askSecret(SECRET_ADMIN, 'Admin API anahtarı (aylık harcama ve token kullanımı için). Boş = sil.', 'sk-ant-admin01-…', (v) =>
        v.startsWith('sk-ant-admin') ? undefined : 'Admin anahtarları "sk-ant-admin" ile başlar.',
      ),
    ),
    vscode.commands.registerCommand('claudeLimits.setOAuthToken', () =>
      askSecret(
        SECRET_OAUTH,
        'Claude Code girişi yerine kullanılacak OAuth token (genelde gerekmez). Boş = sil.',
        'sk-ant-oat01-…',
        () => undefined,
      ),
    ),
    vscode.commands.registerCommand('claudeLimits.clearSecrets', async () => {
      const ok = await vscode.window.showWarningMessage('Kayıtlı tüm Claude Limits anahtarları silinsin mi?', { modal: true }, 'Sil');
      if (ok !== 'Sil') return;
      await Promise.all([SECRET_API, SECRET_ADMIN, SECRET_OAUTH].map((k) => context.secrets.delete(k)));
      await loadSecrets();
      await service.refresh(true);
    }),
    vscode.commands.registerCommand('claudeLimits.openSettings', () =>
      vscode.commands.executeCommand('workbench.action.openSettings', '@ext:caganyl.claude-limits'),
    ),
  );
}

export function deactivate(): void {
  panel?.dispose();
}

function buildTooltip(s: Snapshot): vscode.MarkdownString {
  const md = new vscode.MarkdownString(undefined, true);
  md.supportThemeIcons = true;
  md.isTrusted = { enabledCommands: ['claudeLimits.showDetails', 'claudeLimits.refresh', 'claudeLimits.setApiKey'] };
  md.appendMarkdown('**Claude limitleri**\n\n');
  if (s.subscription?.windows.length) {
    md.appendMarkdown('| Limit | Kullanım | Sıfırlanma |\n|:--|--:|:--|\n');
    for (const w of s.subscription.windows) {
      md.appendMarkdown(`| ${w.label}${w.active ? ' ★' : ''} | ${formatPercent(w.percent)} | ${formatResetIn(w.resetsAt) || '–'} |\n`);
    }
    md.appendMarkdown('\n');
  }
  if (s.subscriptionError) md.appendMarkdown(`$(error) ${escapeMd(s.subscriptionError)}\n\n`);
  if (s.rateLimits?.buckets.length) {
    md.appendMarkdown(`**API** · \`${s.rateLimits.model}\`${s.rateLimits.limited ? ' · $(error) 429' : ''}\n\n`);
    for (const b of s.rateLimits.buckets) {
      md.appendMarkdown(`- ${b.label}: ${b.remaining.toLocaleString('tr-TR')} / ${b.limit.toLocaleString('tr-TR')} kaldı (${formatPercent(b.percentUsed)})\n`);
    }
    md.appendMarkdown('\n');
  }
  if (s.rateLimitsError) md.appendMarkdown(`$(error) API: ${escapeMd(s.rateLimitsError)}\n\n`);
  if (s.cost) {
    md.appendMarkdown(
      `**API harcaması** · bu ay ${formatUsd(s.cost.monthToDateUsd)} · bugün ${formatUsd(s.cost.todayUsd)}` +
        (s.cost.percentOfBudget !== undefined ? ` · bütçe ${formatPercent(s.cost.percentOfBudget)}` : '') +
        '\n\n',
    );
  }
  if (s.costError) md.appendMarkdown(`$(error) Harcama: ${escapeMd(s.costError)}\n\n`);
  md.appendMarkdown('[$(dashboard) Ayrıntılar](command:claudeLimits.showDetails) · [$(refresh) Yenile](command:claudeLimits.refresh)');
  return md;
}

function escapeMd(s: string): string {
  return s.replace(/[\\`*_{}[\]()#+\-.!|<>]/g, (c) => `\\${c}`);
}

function showPanel(context: vscode.ExtensionContext, html: () => string, service: LimitsService): void {
  if (panel) {
    panel.reveal(vscode.ViewColumn.Beside, true);
    void panel.webview.postMessage({ type: 'render', html: html() });
    return;
  }
  panel = vscode.window.createWebviewPanel('claudeLimits.details', 'Claude Limits', { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true }, {
    enableScripts: true,
    retainContextWhenHidden: false,
    localResourceRoots: [],
  });
  panel.iconPath = vscode.Uri.joinPath(context.extensionUri, 'media', 'icon.png');
  const nonce = Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2);
  panel.webview.html = `<!doctype html><html lang="tr"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style nonce="${nonce}">
:root{--cl-font:var(--vscode-font-family);--cl-fg:var(--vscode-foreground);--cl-muted:var(--vscode-descriptionForeground);
--cl-card:var(--vscode-editorWidget-background,var(--vscode-sideBar-background));--cl-border:var(--vscode-widget-border,var(--vscode-panel-border));
--cl-track:var(--vscode-input-background);--cl-ok:var(--vscode-charts-green,#3fb950);--cl-warn:var(--vscode-charts-orange,#d29922);
--cl-crit:var(--vscode-charts-red,#f85149);--cl-accent:var(--vscode-button-background);--cl-on-accent:var(--vscode-button-foreground)}
body{padding:16px;max-width:560px}
${BASE_CSS}
</style></head><body><div id="root">${html()}</div>
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
const root = document.getElementById('root');
window.addEventListener('message', (e) => { if (e.data && e.data.type === 'render') root.innerHTML = e.data.html; });
root.addEventListener('click', (e) => {
  const el = e.target.closest('[data-action]');
  if (el) vscode.postMessage({ action: el.getAttribute('data-action') });
});
</script></body></html>`;
  panel.webview.onDidReceiveMessage((m: { action?: string }) => {
    const map: Record<string, string> = {
      refresh: 'claudeLimits.refresh',
      settings: 'claudeLimits.openSettings',
      'set-api-key': 'claudeLimits.setApiKey',
      'set-admin-key': 'claudeLimits.setAdminKey',
    };
    const cmd = m.action ? map[m.action] : undefined;
    if (cmd) void vscode.commands.executeCommand(cmd);
  });
  const timer = setInterval(() => void panel?.webview.postMessage({ type: 'render', html: html() }), 30_000);
  panel.onDidDispose(() => {
    clearInterval(timer);
    panel = undefined;
  });
  void service;
}
