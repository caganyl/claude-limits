// Node-only: locates the Claude Code OAuth token on this machine.
//   Windows / Linux : %USERPROFILE%\.claude\.credentials.json  (or $CLAUDE_CONFIG_DIR/.credentials.json)
//   macOS           : Keychain item "Claude Code-credentials" (or "…-<sha256(dir)[0..8]>" for a custom config dir),
//                     falling back to the file.
// The token is only read, never refreshed or written: refreshing here would rotate the refresh
// token underneath Claude Code and could log it out.
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { LimitsError } from './api';

export interface OAuthCredentials {
  accessToken: string;
  /** Epoch ms. */
  expiresAt?: number;
  plan?: string;
  /** Human-readable origin, shown in diagnostics. */
  source: string;
}

export interface CredentialOptions {
  /** Token pasted by the user in settings; wins over everything else. */
  manualToken?: string;
  /** Override for ~/.claude (otherwise $CLAUDE_CONFIG_DIR or ~/.claude). */
  configDir?: string;
  env?: NodeJS.ProcessEnv;
  platform?: NodeJS.Platform;
  now?: Date;
  /** Test seam for the macOS keychain. */
  readKeychain?: (service: string) => Promise<string | null>;
}

export function claudeConfigDir(env: NodeJS.ProcessEnv = process.env): string {
  const fromEnv = env.CLAUDE_CONFIG_DIR?.trim();
  return fromEnv ? path.resolve(fromEnv) : path.join(os.homedir(), '.claude');
}

/** Keychain service names Claude Code may use for this config dir, most specific first. */
export function keychainServices(configDir: string | undefined): string[] {
  const base = 'Claude Code-credentials';
  if (!configDir) return [base];
  const hash = createHash('sha256').update(configDir).digest('hex').slice(0, 8);
  return [`${base}-${hash}`, base];
}

function readMacKeychain(service: string): Promise<string | null> {
  return new Promise((resolve, reject) => {
    execFile(
      '/usr/bin/security',
      ['find-generic-password', '-s', service, '-w'],
      { timeout: 20000 },
      (err, stdout) => {
        if (err) {
          const code = (err as unknown as { code?: number | string }).code;
          if (code === 44 || code === '44') return resolve(null); // errSecItemNotFound
          return reject(new LimitsError('auth', `Anahtar Zinciri okunamadı (${err.message.split('\n')[0]}). Erişim isteminde "Her Zaman İzin Ver" seçin.`));
        }
        const v = stdout.trim();
        resolve(v || null);
      },
    );
  });
}

/** Extracts the token from the credentials JSON blob Claude Code stores. */
export function parseCredentialsBlob(raw: string, source: string): OAuthCredentials | null {
  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    return null;
  }
  const oauth = (json as { claudeAiOauth?: Record<string, unknown> })?.claudeAiOauth;
  if (!oauth || typeof oauth.accessToken !== 'string' || !oauth.accessToken) return null;
  const creds: OAuthCredentials = { accessToken: oauth.accessToken, source };
  if (typeof oauth.expiresAt === 'number') creds.expiresAt = oauth.expiresAt;
  if (typeof oauth.subscriptionType === 'string') creds.plan = oauth.subscriptionType;
  return creds;
}

/**
 * Finds a usable OAuth token. Throws LimitsError('no_credentials') when nothing is found and
 * LimitsError('expired') when the stored token has expired (Claude Code refreshes it on next use).
 */
export async function readClaudeCodeCredentials(opts: CredentialOptions = {}): Promise<OAuthCredentials> {
  const env = opts.env ?? process.env;
  const platform = opts.platform ?? process.platform;
  const now = opts.now ?? new Date();

  if (opts.manualToken?.trim()) return { accessToken: opts.manualToken.trim(), source: 'ayarlar (elle girilen token)' };
  if (env.CLAUDE_CODE_OAUTH_TOKEN?.trim()) {
    return { accessToken: env.CLAUDE_CODE_OAUTH_TOKEN.trim(), source: 'CLAUDE_CODE_OAUTH_TOKEN' };
  }

  const customDir = opts.configDir ?? (env.CLAUDE_CONFIG_DIR?.trim() ? path.resolve(env.CLAUDE_CONFIG_DIR) : undefined);
  const dir = customDir ?? path.join(os.homedir(), '.claude');
  const candidates: OAuthCredentials[] = [];

  if (platform === 'darwin') {
    const readKc = opts.readKeychain ?? readMacKeychain;
    for (const svc of keychainServices(customDir)) {
      const raw = await readKc(svc);
      const c = raw ? parseCredentialsBlob(raw, `macOS Anahtar Zinciri (${svc})`) : null;
      if (c) {
        candidates.push(c);
        break;
      }
    }
  }

  const file = path.join(dir, '.credentials.json');
  try {
    const c = parseCredentialsBlob(await fs.readFile(file, 'utf8'), file);
    if (c) candidates.push(c);
  } catch {
    /* missing file is fine */
  }

  if (!candidates.length) {
    throw new LimitsError(
      'no_credentials',
      'Claude Code girişi bulunamadı. Terminalde `claude` çalıştırıp /login ile giriş yapın (veya ayarlardan token girin).',
    );
  }
  // Prefer a non-expired token, then the one that expires last.
  candidates.sort((a, b) => (b.expiresAt ?? Infinity) - (a.expiresAt ?? Infinity));
  const best = candidates[0];
  if (best.expiresAt !== undefined && best.expiresAt < now.getTime()) {
    throw new LimitsError(
      'expired',
      'Claude Code token’ının süresi dolmuş. Claude Code’u (VS Code eklentisi veya terminal) bir kez kullanınca kendiliğinden yenilenir.',
    );
  }
  return best;
}
