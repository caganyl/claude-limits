// End-to-end smoke test: loads the bundled extension with a fake `vscode` module,
// a fake Claude Code login and a mocked Anthropic API, then drives the commands.
// Run: node test/smoke.cjs   (after `npm run build`)
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Module = require('node:module');

const home = fs.mkdtempSync(path.join(os.tmpdir(), 'cl-vscode-home-'));
process.env.HOME = home;
process.env.USERPROFILE = home;
delete process.env.CLAUDE_CONFIG_DIR;
delete process.env.CLAUDE_CODE_OAUTH_TOKEN;
fs.mkdirSync(path.join(home, '.claude'));
fs.writeFileSync(
  path.join(home, '.claude', '.credentials.json'),
  JSON.stringify({ claudeAiOauth: { accessToken: 'tok-smoke', expiresAt: Date.now() + 3600e3, subscriptionType: 'max' } }),
);

// ---------------------------------------------------------------- mocked network
const calls = [];
async function handle(url, init = {}) {
  calls.push({ url: String(url), init });
  const u = new URL(String(url));
  const json = (body, status = 200, headers = {}) =>
    new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
  if (u.pathname === '/api/oauth/usage') {
    assert.equal(init.headers.authorization, 'Bearer tok-smoke');
    return json({
      five_hour: { utilization: 81, resets_at: new Date(Date.now() + 2 * 3600e3).toISOString() },
      seven_day: { utilization: 22, resets_at: new Date(Date.now() + 4 * 86400e3).toISOString() },
      limits: [
        { kind: 'session', percent: 81, resets_at: new Date(Date.now() + 2 * 3600e3).toISOString(), scope: null, is_active: true },
        { kind: 'weekly_all', percent: 22, resets_at: new Date(Date.now() + 4 * 86400e3).toISOString(), scope: null, is_active: false },
      ],
    });
  }
  if (u.pathname === '/v1/messages') {
    assert.equal(init.headers['x-api-key'], 'sk-ant-api03-smoke');
    return json({ content: [] }, 200, {
      'anthropic-ratelimit-requests-limit': '50',
      'anthropic-ratelimit-requests-remaining': '45',
      'anthropic-ratelimit-output-tokens-limit': '8000',
      'anthropic-ratelimit-output-tokens-remaining': '7000',
    });
  }
  return json({ error: { message: 'unexpected ' + u.pathname } }, 404);
}

// Real HTTP server: the extension now uses its own proxy-aware transport, not globalThis.fetch.
const http = require('node:http');
const mock = http.createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const r = await handle(`http://127.0.0.1${req.url}`, { method: req.method, headers: req.headers, body: Buffer.concat(chunks).toString() });
  res.writeHead(r.status, Object.fromEntries(r.headers));
  res.end(Buffer.from(await r.arrayBuffer()));
});
// A corporate-style proxy variable that must be bypassed for this host (NO_PROXY).
process.env.HTTPS_PROXY = 'http://zhlevproxy2.invalid:8080';
process.env.HTTP_PROXY = 'http://zhlevproxy2.invalid:8080';
process.env.NO_PROXY = '127.0.0.1,localhost';

// ---------------------------------------------------------------- fake vscode
const commands = new Map();
const statusItems = [];
const messages = [];
const secretStore = new Map();
const secretListeners = [];
let webviewHtml = '';
const posted = [];
let nextInput;

class ThemeColor { constructor(id) { this.id = id; } }
class MarkdownString {
  constructor(v = '') { this.value = v; }
  appendMarkdown(s) { this.value += s; return this; }
}
const vscode = {
  StatusBarAlignment: { Left: 1, Right: 2 },
  ViewColumn: { Beside: -2 },
  ThemeColor,
  MarkdownString,
  Uri: { joinPath: (...p) => p.join('/') },
  window: {
    createStatusBarItem() {
      const it = { text: '', show() { this.visible = true; }, dispose() {} };
      statusItems.push(it);
      return it;
    },
    showWarningMessage: async (m) => { messages.push(['warn', m]); return undefined; },
    showErrorMessage: async (m) => { messages.push(['error', m]); return undefined; },
    showInformationMessage: async (m) => { messages.push(['info', m]); return undefined; },
    showInputBox: async (opts) => { assert.equal(opts.password, true); return nextInput; },
    createWebviewPanel() {
      return {
        webview: {
          set html(v) { webviewHtml = v; },
          get html() { return webviewHtml; },
          postMessage: async (m) => { posted.push(m); return true; },
          onDidReceiveMessage() { return { dispose() {} }; },
        },
        reveal() {},
        onDidDispose() { return { dispose() {} }; },
        dispose() {},
      };
    },
  },
  workspace: {
    getConfiguration: () => ({ get: (_k, d) => d }),
    onDidChangeConfiguration: () => ({ dispose() {} }),
  },
  commands: {
    registerCommand: (id, fn) => { commands.set(id, fn); return { dispose() {} }; },
    executeCommand: async (id, ...a) => commands.get(id)?.(...a),
  },
};
const origLoad = Module._load;
Module._load = function (request, ...rest) {
  if (request === 'vscode') return vscode;
  return origLoad.call(this, request, ...rest);
};

const globalState = new Map();
const context = {
  subscriptions: [],
  extensionUri: 'ext',
  globalState: { get: (k, d) => (globalState.has(k) ? globalState.get(k) : d), update: async (k, v) => globalState.set(k, v) },
  secrets: {
    get: async (k) => secretStore.get(k),
    store: async (k, v) => { secretStore.set(k, v); secretListeners.forEach((l) => l({ key: k })); },
    delete: async (k) => { secretStore.delete(k); secretListeners.forEach((l) => l({ key: k })); },
    onDidChange: (l) => { secretListeners.push(l); return { dispose() {} }; },
  },
};

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  await new Promise((r) => mock.listen(0, '127.0.0.1', r));
  process.env.CLAUDE_LIMITS_API_BASE = `http://127.0.0.1:${mock.address().port}`;
  const ext = require(path.join(__dirname, '..', 'dist', 'extension.js'));
  await ext.activate(context);
  await sleep(300);

  const item = statusItems[0];
  assert.ok(item.visible, 'status bar item is shown');
  assert.match(item.text, /5s 81% · 7g 22%/, `status text was: ${item.text}`);
  assert.equal(item.backgroundColor?.id, 'statusBarItem.warningBackground');
  assert.match(item.tooltip.value, /Oturum \(5 saat\) ★ \| 81%/);
  assert.ok(messages.some(([lvl, m]) => lvl === 'warn' && /Oturum \(5 saat\) limiti 81%/.test(m)), 'threshold notification raised');
  assert.ok(fs.existsSync(path.join(home, '.claude-limits', 'cache.json')), 'shared cache written');

  for (const id of ['claudeLimits.showDetails', 'claudeLimits.refresh', 'claudeLimits.setApiKey', 'claudeLimits.setAdminKey', 'claudeLimits.clearSecrets']) {
    assert.ok(commands.has(id), `command ${id} registered`);
  }

  await vscode.commands.executeCommand('claudeLimits.showDetails');
  assert.match(webviewHtml, /Content-Security-Policy/);
  // Bar widths are inline style attributes: the CSP must allow them (no nonce on style-src).
  assert.match(webviewHtml, /style-src 'unsafe-inline'/);
  assert.match(webviewHtml, /class="cl-fill cl-warn" style="width:81\.0%"/);
  assert.match(webviewHtml, /Abonelik limitleri/);
  assert.match(webviewHtml, /bağlayıcı/);
  assert.match(webviewHtml, /data-action="set-api-key"/);

  nextInput = 'sk-ant-api03-smoke';
  await vscode.commands.executeCommand('claudeLimits.setApiKey');
  await sleep(300);
  assert.equal(secretStore.get('claudeLimits.apiKey'), 'sk-ant-api03-smoke');
  assert.ok(calls.some((c) => c.url.endsWith('/v1/messages')), 'rate limit probe sent');
  const last = posted[posted.length - 1];
  assert.match(last.html, /İstek \/ dk/);
  assert.match(last.html, /45 \/ 50 kaldı/);
  assert.match(item.tooltip.value, /claude-haiku-5-5/);

  const usageCalls = calls.filter((c) => c.url.endsWith('/api/oauth/usage')).length;
  await vscode.commands.executeCommand('claudeLimits.refresh');
  assert.equal(calls.filter((c) => c.url.endsWith('/api/oauth/usage')).length, usageCalls, '15 s floor prevents refetch spam');

  for (const d of context.subscriptions) d.dispose?.();
  ext.deactivate();
  if (process.env.DUMP_WEBVIEW) fs.writeFileSync(process.env.DUMP_WEBVIEW, webviewHtml.replace('acquireVsCodeApi()', '({postMessage(){}})').replace('<body>', '<body style="background:#1f1f1f;--vscode-foreground:#ccc;--vscode-descriptionForeground:#9d9d9d;--vscode-editorWidget-background:#252526;--vscode-widget-border:#3c3c3c;--vscode-input-background:#3c3c3c;--vscode-button-background:#0e639c;--vscode-button-foreground:#fff">'));
  console.log('SMOKE OK —', calls.length, 'HTTP calls; status:', item.text);
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
