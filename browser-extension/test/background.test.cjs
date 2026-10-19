// Runs the built service worker in a VM with a fake `chrome` API and mocked network.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const code = fs.readFileSync(path.join(__dirname, '..', 'build', 'background.js'), 'utf8');
assert.ok(!/require\(["']node:|require\(["'](fs|crypto|child_process)["']\)/.test(code), 'no Node built-ins in the browser bundle');

const listeners = {};
const on = (name) => ({ addListener: (fn) => (listeners[name] = fn) });
const local = new Map();
const changeListeners = [];
const badge = {};
const notifications = [];
const calls = [];

const chrome = {
  runtime: { onInstalled: on('installed'), onStartup: on('startup'), onMessage: on('message') },
  alarms: { get: async () => undefined, create: async () => {}, onAlarm: on('alarm') },
  notifications: { create: async (id, o) => notifications.push([id, o]), onClicked: on('notifClick') },
  tabs: { create: async () => {} },
  cookies: { get: async ({ name }) => (name === 'lastActiveOrg' ? { value: 'org-personal' } : null) },
  action: {
    setBadgeText: async ({ text }) => (badge.text = text),
    setBadgeBackgroundColor: async ({ color }) => (badge.color = color),
    setBadgeTextColor: async () => {},
    setTitle: async ({ title }) => (badge.title = title),
  },
  storage: {
    local: {
      get: async (k) => (local.has(k) ? { [k]: structuredClone(local.get(k)) } : {}),
      set: async (o) => {
        for (const [k, v] of Object.entries(o)) {
          local.set(k, structuredClone(v));
          changeListeners.forEach((l) => l({ [k]: { newValue: v } }, 'local'));
        }
      },
    },
    onChanged: { addListener: (fn) => changeListeners.push(fn) },
  },
};

const json = (body, status = 200, headers = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });

async function fakeFetch(url, init = {}) {
  calls.push({ url: String(url), init });
  const u = new URL(String(url));
  if (u.host === 'claude.ai') {
    assert.equal(init.credentials, 'include', 'claude.ai calls carry the browser session');
    if (u.pathname === '/api/organizations') {
      return json([
        { uuid: 'org-api', name: 'API org', capabilities: ['api'] },
        { uuid: 'org-personal', name: 'Me', capabilities: ['chat', 'claude_max'] },
      ]);
    }
    if (u.pathname === '/api/organizations/org-personal/usage') {
      return json({
        five_hour: { utilization: 91, resets_at: new Date(Date.now() + 3600e3).toISOString() },
        seven_day: { utilization: 30, resets_at: new Date(Date.now() + 3 * 86400e3).toISOString() },
      });
    }
  }
  if (u.host === 'api.anthropic.com' && u.pathname === '/v1/messages') {
    assert.equal(init.headers['anthropic-dangerous-direct-browser-access'], 'true');
    return json({}, 200, { 'anthropic-ratelimit-requests-limit': '50', 'anthropic-ratelimit-requests-remaining': '10' });
  }
  return json({ error: { message: 'unexpected ' + url } }, 404);
}

const ctx = vm.createContext({
  chrome, fetch: fakeFetch, Response, URL, URLSearchParams, AbortController, setTimeout, clearTimeout,
  console, structuredClone, Date, Math, JSON, Promise,
});
vm.runInContext(code, ctx, { filename: 'background.js' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const send = (msg) => new Promise((res) => listeners.message(msg, {}, res));

(async () => {
  listeners.installed();
  await sleep(200);
  assert.equal(badge.text, '91', 'badge shows the session percent');
  assert.equal(badge.color, '#c2412d', 'critical colour');
  assert.match(badge.title, /Oturum \(5 saat\): %91/);
  assert.ok(calls.some((c) => c.url.endsWith('/org-personal/usage')), 'last active org used');
  assert.equal(notifications.length, 1);
  assert.match(notifications[0][1].message, /Oturum \(5 saat\) limiti 91%/);

  // A forced refresh within 15 s does not hit the network again.
  const before = calls.length;
  await send({ type: 'refresh', force: true });
  assert.equal(calls.length, before);

  // Adding an API key triggers a refresh that probes rate limits.
  await chrome.storage.local.set({ settings: { apiKey: 'sk-ant-api03-x' } });
  await sleep(200);
  const state = local.get('state');
  assert.equal(state.snapshot.rateLimits.buckets[0].percentUsed, 80);
  assert.equal(notifications.length, 2, 'rate-limit warning raised');

  console.log('BROWSER BACKGROUND OK —', calls.length, 'HTTP calls, badge', badge.text);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
