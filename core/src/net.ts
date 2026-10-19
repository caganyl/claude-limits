// Node-only network transport that behaves like a corporate-network-aware `claude`:
//  - honours HTTPS_PROXY / HTTP_PROXY / NO_PROXY (or an explicit proxy setting),
//  - trusts the OS certificate store plus NODE_EXTRA_CA_CERTS (TLS-inspecting proxies),
//  - in "auto" mode falls back to a direct connection when the proxy can't be reached
//    (e.g. in the office the proxy works; at home its host doesn't resolve) and remembers
//    whichever route worked.
import { readFileSync } from 'node:fs';
import * as tls from 'node:tls';
import { Agent, ProxyAgent, fetch as undiciFetch } from 'undici';

/** "auto" (env proxy with direct fallback) | "direct" | "http://host:port". */
export type ProxySetting = string;

export interface NodeFetchOptions {
  /** Read on every request so a settings change applies immediately. */
  proxy: () => ProxySetting | undefined;
  /** Proxy to use in "auto" mode instead of the environment variables (e.g. VS Code's http.proxy). */
  autoProxy?: () => string | undefined;
  env?: NodeJS.ProcessEnv;
  /** Remember the working route for this long. */
  routeTtlMs?: number;
  connectTimeoutMs?: number;
}

export type Route = { kind: 'proxy'; url: string } | { kind: 'direct' };

const PEM_RE = /-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g;

/** Bundled roots + OS store (Node 22.15+/23.8+) + NODE_EXTRA_CA_CERTS. */
export function loadCaCertificates(env: NodeJS.ProcessEnv = process.env): string[] {
  const set = new Set<string>();
  const t = tls as unknown as { getCACertificates?: (kind: string) => string[]; rootCertificates: readonly string[] };
  const add = (list: readonly string[] | undefined) => list?.forEach((c) => set.add(c.trim()));
  if (typeof t.getCACertificates === 'function') {
    for (const kind of ['default', 'system', 'extra']) {
      try {
        add(t.getCACertificates(kind));
      } catch {
        /* kind not supported on this Node */
      }
    }
  } else {
    add(t.rootCertificates);
  }
  const extra = env.NODE_EXTRA_CA_CERTS?.trim();
  if (extra) {
    try {
      add(readFileSync(extra, 'utf8').match(PEM_RE) ?? []);
    } catch {
      /* unreadable file: ignore, the proxy error will say so */
    }
  }
  return [...set];
}

function envProxy(env: NodeJS.ProcessEnv): string | undefined {
  const v = env.HTTPS_PROXY || env.https_proxy || env.HTTP_PROXY || env.http_proxy;
  return v?.trim() || undefined;
}

/** True when NO_PROXY says this host must not go through the proxy. */
export function bypassesProxy(host: string, env: NodeJS.ProcessEnv): boolean {
  const list = (env.NO_PROXY || env.no_proxy || '').split(/[,\s]+/).filter(Boolean);
  const h = host.toLowerCase();
  return list.some((raw) => {
    const e = raw.toLowerCase().replace(/:\d+$/, '');
    if (e === '*') return true;
    const bare = e.replace(/^\*?\./, '');
    return h === bare || h.endsWith(`.${bare}`);
  });
}

function normalizeProxyUrl(u: string): string {
  return /^[a-z]+:\/\//i.test(u) ? u : `http://${u}`;
}

/** Routes to try, in order, for a given host and setting. */
export function routesFor(host: string, setting: ProxySetting | undefined, autoProxy: string | undefined, env: NodeJS.ProcessEnv): Route[] {
  const s = (setting ?? 'auto').trim();
  if (s.toLowerCase() === 'direct') return [{ kind: 'direct' }];
  if (s && s.toLowerCase() !== 'auto') return [{ kind: 'proxy', url: normalizeProxyUrl(s) }];
  const proxy = autoProxy || envProxy(env);
  if (!proxy || bypassesProxy(host, env)) return [{ kind: 'direct' }];
  return [{ kind: 'proxy', url: normalizeProxyUrl(proxy) }, { kind: 'direct' }];
}

const routeKey = (r: Route) => (r.kind === 'direct' ? 'direct' : `proxy:${r.url}`);

/** Errors that mean "this route didn't get us to the server" (as opposed to an HTTP answer). */
function isRouteFailure(e: unknown): boolean {
  const text: string[] = [];
  let cur: unknown = e;
  for (let i = 0; cur && i < 5; i++) {
    const err = cur as { name?: string; code?: string; message?: string; cause?: unknown };
    if (err.name === 'AbortError') return false; // caller's overall timeout: don't try another route
    text.push(String(err.code ?? ''), String(err.message ?? ''));
    cur = err.cause;
  }
  return /ENOTFOUND|EAI_AGAIN|ECONNREFUSED|ECONNRESET|ETIMEDOUT|EHOSTUNREACH|ENETUNREACH|UND_ERR_CONNECT_TIMEOUT|UND_ERR_SOCKET|proxy|tunnel|socket/i.test(
    text.join(' '),
  );
}

export interface NodeFetch {
  (input: string | URL, init?: RequestInit): Promise<Response>;
  /** The route that last worked, for diagnostics. */
  lastRoute(): string | undefined;
}

export function createNodeFetch(opts: NodeFetchOptions): NodeFetch {
  const env = opts.env ?? process.env;
  const ttl = opts.routeTtlMs ?? 10 * 60_000;
  const connectTimeout = opts.connectTimeoutMs ?? 6000;
  let ca: string[] | undefined;
  const caList = () => (ca ??= loadCaCertificates(env));
  const dispatchers = new Map<string, Agent | ProxyAgent>();
  let preferred: { key: string; until: number } | undefined;
  let last: string | undefined;

  const dispatcherFor = (r: Route) => {
    const key = routeKey(r);
    let d = dispatchers.get(key);
    if (!d) {
      const tlsOpts = { ca: caList(), timeout: connectTimeout };
      d =
        r.kind === 'direct'
          ? new Agent({ connect: tlsOpts })
          : new ProxyAgent({ uri: r.url, requestTls: { ca: caList() }, proxyTls: { ca: caList() }, connect: { timeout: connectTimeout } } as ConstructorParameters<typeof ProxyAgent>[0]);
      dispatchers.set(key, d);
    }
    return d;
  };

  const fn = (async (input: string | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    let routes = routesFor(url.hostname, opts.proxy(), opts.autoProxy?.(), env);
    if (preferred && preferred.until > Date.now() && routes.length > 1) {
      const key = preferred.key;
      routes = [...routes.filter((r) => routeKey(r) === key), ...routes.filter((r) => routeKey(r) !== key)];
    }
    let lastErr: unknown;
    for (let i = 0; i < routes.length; i++) {
      const r = routes[i];
      try {
        const res = await undiciFetch(url, { ...(init as object), dispatcher: dispatcherFor(r) } as Parameters<typeof undiciFetch>[1]);
        last = routeKey(r);
        if (routes.length > 1) preferred = { key: last, until: Date.now() + ttl };
        return res as unknown as Response;
      } catch (e) {
        lastErr = e;
        if (i === routes.length - 1 || !isRouteFailure(e)) break;
      }
    }
    preferred = undefined;
    throw lastErr;
  }) as NodeFetch;
  fn.lastRoute = () => last;
  return fn;
}
