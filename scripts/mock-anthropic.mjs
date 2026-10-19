// Local mock of the Anthropic endpoints Claude Limits uses — for manual and automated testing.
//   node scripts/mock-anthropic.mjs [port]
//   CLAUDE_LIMITS_API_BASE=http://127.0.0.1:8787 <run any client>
import { createServer } from 'node:http';

const port = Number(process.argv[2] ?? 8787);
const inH = (h) => new Date(Date.now() + h * 3600e3).toISOString();

const usage = () => ({
  five_hour: { utilization: 63, resets_at: inH(2.3) },
  seven_day: { utilization: 41, resets_at: inH(83) },
  seven_day_opus: null,
  seven_day_sonnet: null,
  extra_usage: { is_enabled: true, monthly_limit: 5000, used_credits: 0, utilization: 12 },
  limits: [
    { kind: 'session', percent: 63, resets_at: inH(2.3), scope: null, is_active: false },
    { kind: 'weekly_all', percent: 41, resets_at: inH(83), scope: null, is_active: false },
    { kind: 'weekly_scoped', percent: 82, resets_at: inH(60), scope: { model: { id: null, display_name: 'Opus' } }, is_active: true },
  ],
});

const server = createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${port}`);
  const send = (status, body, headers = {}) => {
    res.writeHead(status, { 'content-type': 'application/json', ...headers });
    res.end(JSON.stringify(body));
  };
  process.stdout.write(`${req.method} ${url.pathname}\n`);
  if (url.pathname === '/api/oauth/usage') {
    if (!/^Bearer \S+/.test(req.headers.authorization ?? '')) return send(401, { error: { message: 'missing token' } });
    return send(200, usage());
  }
  if (url.pathname === '/v1/messages' && req.method === 'POST') {
    if (!req.headers['x-api-key']) return send(401, { error: { message: 'invalid x-api-key' } });
    req.resume();
    return send(200, { id: 'msg_mock', content: [{ type: 'text', text: '.' }] }, {
      'anthropic-ratelimit-requests-limit': '1000',
      'anthropic-ratelimit-requests-remaining': '962',
      'anthropic-ratelimit-requests-reset': new Date(Date.now() + 40e3).toISOString(),
      'anthropic-ratelimit-input-tokens-limit': '400000',
      'anthropic-ratelimit-input-tokens-remaining': '251000',
      'anthropic-ratelimit-input-tokens-reset': new Date(Date.now() + 25e3).toISOString(),
      'anthropic-ratelimit-output-tokens-limit': '80000',
      'anthropic-ratelimit-output-tokens-remaining': '12000',
      'anthropic-ratelimit-output-tokens-reset': new Date(Date.now() + 50e3).toISOString(),
    });
  }
  if (url.pathname === '/v1/organizations/cost_report') {
    const start = new Date(url.searchParams.get('starting_at'));
    const data = [];
    for (let d = new Date(start); d < new Date(); d = new Date(d.getTime() + 86400e3)) {
      data.push({ starting_at: d.toISOString().replace('.000', ''), ending_at: '', results: [{ currency: 'USD', amount: '412.5' }] });
    }
    return send(200, { data, has_more: false, next_page: null });
  }
  if (url.pathname === '/v1/organizations/usage_report/messages') {
    return send(200, {
      data: [{ starting_at: url.searchParams.get('starting_at'), results: [{ uncached_input_tokens: 182000, cache_read_input_tokens: 940000, cache_creation: { ephemeral_5m_input_tokens: 64000 }, output_tokens: 51000 }] }],
      has_more: false,
    });
  }
  send(404, { error: { message: `not mocked: ${url.pathname}` } });
});
server.listen(port, '127.0.0.1', () => console.log(`mock anthropic on http://127.0.0.1:${port}`));
