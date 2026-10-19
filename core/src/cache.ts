// Node-only: a small cache file shared by every Claude Limits client on this machine
// (each VS Code window, the tray app and the Visual Studio extension), so several
// open editors don't each hammer the usage endpoint.
//
// Location: ~/.claude-limits/cache.json  — format documented in docs/cache-format.md.
import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

export interface CacheEntry<T> {
  /** ISO time of the last attempt (success or failure). */
  fetchedAt: string;
  data?: T;
  error?: string;
  errorKind?: string;
  /** ISO time before which nobody should call the endpoint again (after HTTP 429). */
  backoffUntil?: string;
  /** Identifies the credential the entry belongs to (hash, never the secret). */
  keyId?: string;
}

export interface CacheFile {
  version: 1;
  subscription?: CacheEntry<unknown>;
  rateLimits?: CacheEntry<unknown>;
  cost?: CacheEntry<unknown>;
}

export type CacheSection = 'subscription' | 'rateLimits' | 'cost';

export function defaultCacheDir(): string {
  return path.join(os.homedir(), '.claude-limits');
}

/** Short, non-reversible id for a secret so entries for different keys don't mix. */
export function keyId(secret: string, extra = ''): string {
  return createHash('sha256').update(secret).digest('hex').slice(0, 12) + (extra ? `|${extra}` : '');
}

export class SharedCache {
  readonly file: string;

  constructor(dir: string = defaultCacheDir()) {
    this.file = path.join(dir, 'cache.json');
  }

  async read(): Promise<CacheFile> {
    try {
      const json = JSON.parse(await fs.readFile(this.file, 'utf8')) as CacheFile;
      if (json && json.version === 1) return json;
    } catch {
      /* missing or corrupt — start fresh */
    }
    return { version: 1 };
  }

  async get<T>(section: CacheSection): Promise<CacheEntry<T> | undefined> {
    return (await this.read())[section] as CacheEntry<T> | undefined;
  }

  async put<T>(section: CacheSection, entry: CacheEntry<T>): Promise<void> {
    const current = await this.read();
    (current as unknown as Record<string, unknown>)[section] = entry;
    await fs.mkdir(path.dirname(this.file), { recursive: true });
    const tmp = `${this.file}.${process.pid}.${Date.now()}.tmp`;
    await fs.writeFile(tmp, JSON.stringify(current, null, 2), { encoding: 'utf8', mode: 0o600 });
    try {
      await fs.rename(tmp, this.file);
    } catch {
      // Windows can refuse a rename over a file another process has open; fall back to a direct write.
      await fs.writeFile(this.file, JSON.stringify(current, null, 2), { encoding: 'utf8', mode: 0o600 });
      await fs.rm(tmp, { force: true });
    }
  }
}
