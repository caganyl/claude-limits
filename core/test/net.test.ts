import { strict as assert } from 'node:assert';
import { createServer, request as httpRequest, Server } from 'node:http';
import { AddressInfo, connect as netConnect } from 'node:net';
import { test } from 'node:test';
import { LimitsError, bypassesProxy, createNodeFetch, describeNetworkError, fetchSubscriptionUsage, routesFor } from '../src/index';

function listen(server: Server): Promise<number> {
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r((server.address() as AddressInfo).port)));
}

/** Upstream that plays api.anthropic.com. */
function upstream(): Server {
  return createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'application/json', 'x-via': String(req.headers['x-seen-proxy'] ?? 'direct') });
    res.end(JSON.stringify({ five_hour: { utilization: 12, resets_at: null }, path: req.url }));
  });
}

/** Minimal forward proxy (absolute-URI GET + CONNECT), counts what it carries. */
function proxyServer(counter: { n: number }): Server {
  const s = createServer((req, res) => {
    counter.n++;
    const u = new URL(req.url!);
    const up = httpRequest(
      { host: u.hostname, port: u.port, path: u.pathname + u.search, method: req.method, headers: { ...req.headers, 'x-seen-proxy': 'proxy' } },
      (r) => {
        res.writeHead(r.statusCode ?? 502, r.headers);
        r.pipe(res);
      },
    );
    req.pipe(up);
  });
  s.on('connect', (req, socket, head) => {
    counter.n++;
    const [host, port] = req.url!.split(':');
    const up = netConnect(Number(port), host, () => {
      socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      up.write(head);
      up.pipe(socket);
      socket.pipe(up);
    });
    up.on('error', () => socket.destroy());
  });
  return s;
}

test('routesFor: auto uses env proxy then direct; NO_PROXY and explicit settings', () => {
  const env = { HTTPS_PROXY: 'http://zhlevproxy2:8080', NO_PROXY: 'localhost,.corp.local' };
  assert.deepEqual(routesFor('api.anthropic.com', 'auto', undefined, env), [
    { kind: 'proxy', url: 'http://zhlevproxy2:8080' },
    { kind: 'direct' },
  ]);
  assert.deepEqual(routesFor('api.anthropic.com', 'direct', undefined, env), [{ kind: 'direct' }]);
  assert.deepEqual(routesFor('api.anthropic.com', 'proxy.x:3128', undefined, env), [{ kind: 'proxy', url: 'http://proxy.x:3128' }]);
  assert.deepEqual(routesFor('api.anthropic.com', 'auto', undefined, {}), [{ kind: 'direct' }]);
  assert.deepEqual(routesFor('api.anthropic.com', 'auto', 'http://vscode-proxy:1', {}), [
    { kind: 'proxy', url: 'http://vscode-proxy:1' },
    { kind: 'direct' },
  ]);
  assert.equal(bypassesProxy('git.corp.local', env), true);
  assert.equal(bypassesProxy('api.anthropic.com', env), false);
});

test('office: requests go through the env proxy', async () => {
  const up = upstream();
  const counter = { n: 0 };
  const px = proxyServer(counter);
  const upPort = await listen(up);
  const pxPort = await listen(px);
  try {
    const f = createNodeFetch({ proxy: () => 'auto', env: { HTTP_PROXY: `http://127.0.0.1:${pxPort}` } });
    const res = await f(`http://127.0.0.1:${upPort}/api/oauth/usage`);
    assert.equal(res.status, 200);
    assert.equal(counter.n, 1, 'the request went through the proxy (CONNECT tunnel)');
    assert.match(f.lastRoute() ?? '', /^proxy:/);
  } finally {
    up.close();
    px.close();
  }
});

test('home: unreachable proxy falls back to direct and the route is remembered', async () => {
  const up = upstream();
  const upPort = await listen(up);
  try {
    // Nothing listens on this port: like zhlevproxy2:8080 outside the office.
    const dead = createServer();
    const deadPort = await listen(dead);
    dead.close();
    const f = createNodeFetch({ proxy: () => 'auto', env: { HTTPS_PROXY: `http://127.0.0.1:${deadPort}`, HTTP_PROXY: `http://127.0.0.1:${deadPort}` } });
    const res = await f(`http://127.0.0.1:${upPort}/x`);
    assert.equal(res.headers.get('x-via'), 'direct');
    assert.equal(f.lastRoute(), 'direct');
    const t0 = Date.now();
    await f(`http://127.0.0.1:${upPort}/y`);
    assert.ok(Date.now() - t0 < 1000, 'second call goes direct straight away');
  } finally {
    up.close();
  }
});

test('explicit "direct" never touches the proxy; explicit proxy has no fallback', async () => {
  const up = upstream();
  const counter = { n: 0 };
  const px = proxyServer(counter);
  const upPort = await listen(up);
  const pxPort = await listen(px);
  try {
    const env = { HTTP_PROXY: `http://127.0.0.1:${pxPort}` };
    const direct = createNodeFetch({ proxy: () => 'direct', env });
    assert.equal((await direct(`http://127.0.0.1:${upPort}/`)).headers.get('x-via'), 'direct');
    assert.equal(counter.n, 0);

    const dead = createServer();
    const deadPort = await listen(dead);
    dead.close();
    const strict = createNodeFetch({ proxy: () => `127.0.0.1:${deadPort}`, env: {} });
    await assert.rejects(strict(`http://127.0.0.1:${upPort}/`));
  } finally {
    up.close();
    px.close();
  }
});

test('the API layer reports the real cause instead of "fetch failed"', async () => {
  const dead = createServer();
  const deadPort = await listen(dead);
  dead.close();
  const prev = process.env.CLAUDE_LIMITS_API_BASE;
  process.env.CLAUDE_LIMITS_API_BASE = `http://127.0.0.1:${deadPort}`;
  try {
    const f = createNodeFetch({ proxy: () => 'direct', env: {} });
    await assert.rejects(fetchSubscriptionUsage('tok', { fetchFn: f as typeof fetch }), (e: unknown) => {
      const le = e as LimitsError;
      return le.kind === 'network' && /Bağlantı kurulamadı/.test(le.message) && /ECONNREFUSED/.test(le.message);
    });
  } finally {
    if (prev === undefined) delete process.env.CLAUDE_LIMITS_API_BASE;
    else process.env.CLAUDE_LIMITS_API_BASE = prev;
  }
  const certErr = Object.assign(new TypeError('fetch failed'), {
    cause: Object.assign(new Error('unable to get local issuer certificate'), { code: 'UNABLE_TO_GET_ISSUER_CERT_LOCALLY' }),
  });
  assert.match(describeNetworkError(certErr), /^Şirket SSL sertifikası tanınmıyor/);
  const dns = Object.assign(new TypeError('fetch failed'), { cause: Object.assign(new Error('getaddrinfo ENOTFOUND zhlevproxy2'), { code: 'ENOTFOUND' }) });
  assert.match(describeNetworkError(dns), /çözülemedi/);
});

test('TLS-inspecting proxy: certificate from NODE_EXTRA_CA_CERTS is trusted', async (t) => {
  const { execFileSync } = await import('node:child_process');
  const { mkdtempSync, readFileSync } = await import('node:fs');
  const { tmpdir } = await import('node:os');
  const { join } = await import('node:path');
  const https = await import('node:https');
  const dir = mkdtempSync(join(tmpdir(), 'cl-ca-'));
  try {
    execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2', '-subj', '/CN=Corp Inspection CA',
      '-addext', 'subjectAltName=IP:127.0.0.1', '-keyout', join(dir, 'key.pem'), '-out', join(dir, 'cert.pem')], { stdio: 'ignore' });
  } catch {
    t.skip('openssl not available');
    return;
  }
  const server = https.createServer({ key: readFileSync(join(dir, 'key.pem')), cert: readFileSync(join(dir, 'cert.pem')) }, (_q, s) => {
    s.writeHead(200, { 'content-type': 'application/json' });
    s.end('{}');
  });
  const port = await listen(server as unknown as Server);
  try {
    const trusting = createNodeFetch({ proxy: () => 'direct', env: { NODE_EXTRA_CA_CERTS: join(dir, 'cert.pem') } });
    assert.equal((await trusting(`https://127.0.0.1:${port}/`)).status, 200);
    const plain = createNodeFetch({ proxy: () => 'direct', env: {} });
    await assert.rejects(plain(`https://127.0.0.1:${port}/`), (e: unknown) => /^Şirket SSL sertifikası tanınmıyor/.test(describeNetworkError(e)));
  } finally {
    server.close();
  }
});
