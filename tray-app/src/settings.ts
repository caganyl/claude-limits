// Settings persisted in the user-data folder. Secrets are encrypted with Electron safeStorage
// (Windows: DPAPI, macOS: Keychain-protected key) and never written in plain text when that's available.
import { app, safeStorage } from 'electron';
import { promises as fs, readFileSync } from 'node:fs';
import * as path from 'node:path';
import { DEFAULT_CONFIG, ServiceConfig } from '../../core/src/service';
import type { PublicSettings, SettingsUpdate } from './shared';

interface StoredSettings {
  subscriptionEnabled: boolean;
  displayMode: PublicSettings['displayMode'];
  warn: number;
  crit: number;
  subscriptionIntervalSec: number;
  rateLimitIntervalSec: number;
  costIntervalSec: number;
  probeModel: string;
  monthlyBudgetUsd: number;
  notifications: boolean;
  launchAtLogin: boolean;
  menuBarText: boolean;
  proxy: string;
  secrets: { apiKey?: string; adminKey?: string; oauthToken?: string };
}

const DEFAULTS: StoredSettings = {
  subscriptionEnabled: true,
  displayMode: 'both',
  warn: 75,
  crit: 90,
  subscriptionIntervalSec: DEFAULT_CONFIG.subscriptionIntervalSec,
  rateLimitIntervalSec: DEFAULT_CONFIG.rateLimitIntervalSec,
  costIntervalSec: DEFAULT_CONFIG.costIntervalSec,
  probeModel: DEFAULT_CONFIG.probeModel,
  monthlyBudgetUsd: 0,
  notifications: true,
  launchAtLogin: true,
  menuBarText: true,
  proxy: 'auto',
  secrets: {},
};

const clampNum = (v: unknown, min: number, max: number, fallback: number) => {
  const n = Number(v);
  return Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : fallback;
};

export class SettingsStore {
  private data: StoredSettings = { ...DEFAULTS, secrets: {} };
  private readonly file = path.join(app.getPath('userData'), 'settings.json');

  load(): void {
    try {
      const raw = JSON.parse(readFileSync(this.file, 'utf8')) as Partial<StoredSettings>;
      this.data = { ...DEFAULTS, ...raw, secrets: { ...(raw.secrets ?? {}) } };
    } catch {
      this.data = { ...DEFAULTS, secrets: {} };
    }
  }

  private async save(): Promise<void> {
    await fs.mkdir(path.dirname(this.file), { recursive: true });
    await fs.writeFile(this.file, JSON.stringify(this.data, null, 2), { encoding: 'utf8', mode: 0o600 });
  }

  get secure(): boolean {
    try {
      return safeStorage.isEncryptionAvailable();
    } catch {
      return false;
    }
  }

  private encrypt(v: string): string {
    if (this.secure) return 'enc:' + safeStorage.encryptString(v).toString('base64');
    return 'plain:' + Buffer.from(v, 'utf8').toString('base64');
  }

  private decrypt(v: string | undefined): string | undefined {
    if (!v) return undefined;
    try {
      if (v.startsWith('enc:')) return safeStorage.decryptString(Buffer.from(v.slice(4), 'base64'));
      if (v.startsWith('plain:')) return Buffer.from(v.slice(6), 'base64').toString('utf8');
    } catch {
      /* key unreadable (e.g. copied to another machine) */
    }
    return undefined;
  }

  get launchAtLogin(): boolean {
    return this.data.launchAtLogin;
  }
  get displayMode() {
    return this.data.displayMode;
  }
  get notifications(): boolean {
    return this.data.notifications;
  }
  get menuBarText(): boolean {
    return this.data.menuBarText;
  }
  get proxy(): string {
    return this.data.proxy || 'auto';
  }
  get thresholds() {
    return { warn: Math.min(this.data.warn, this.data.crit), crit: Math.max(this.data.warn, this.data.crit) };
  }

  serviceConfig(): ServiceConfig {
    const d = this.data;
    return {
      subscriptionEnabled: d.subscriptionEnabled,
      manualOAuthToken: this.decrypt(d.secrets.oauthToken),
      apiKey: this.decrypt(d.secrets.apiKey),
      adminKey: this.decrypt(d.secrets.adminKey),
      probeModel: d.probeModel || DEFAULT_CONFIG.probeModel,
      monthlyBudgetUsd: d.monthlyBudgetUsd > 0 ? d.monthlyBudgetUsd : undefined,
      subscriptionIntervalSec: d.subscriptionIntervalSec,
      rateLimitIntervalSec: d.rateLimitIntervalSec,
      costIntervalSec: d.costIntervalSec,
    };
  }

  public(): PublicSettings {
    const d = this.data;
    return {
      subscriptionEnabled: d.subscriptionEnabled,
      displayMode: d.displayMode,
      warn: d.warn,
      crit: d.crit,
      subscriptionIntervalSec: d.subscriptionIntervalSec,
      rateLimitIntervalSec: d.rateLimitIntervalSec,
      costIntervalSec: d.costIntervalSec,
      probeModel: d.probeModel,
      monthlyBudgetUsd: d.monthlyBudgetUsd,
      notifications: d.notifications,
      launchAtLogin: d.launchAtLogin,
      menuBarText: d.menuBarText,
      proxy: d.proxy || 'auto',
      hasApiKey: !!this.decrypt(d.secrets.apiKey),
      hasAdminKey: !!this.decrypt(d.secrets.adminKey),
      hasOAuthToken: !!this.decrypt(d.secrets.oauthToken),
      secureStorage: this.secure,
      platform: process.platform,
      version: app.getVersion(),
    };
  }

  async update(u: SettingsUpdate): Promise<PublicSettings> {
    const d = this.data;
    if (typeof u.subscriptionEnabled === 'boolean') d.subscriptionEnabled = u.subscriptionEnabled;
    if (u.displayMode && ['both', 'session', 'weekly', 'highest'].includes(u.displayMode)) d.displayMode = u.displayMode;
    if (u.warn !== undefined) d.warn = clampNum(u.warn, 1, 100, d.warn);
    if (u.crit !== undefined) d.crit = clampNum(u.crit, 1, 100, d.crit);
    if (u.subscriptionIntervalSec !== undefined) d.subscriptionIntervalSec = clampNum(u.subscriptionIntervalSec, 60, 86400, d.subscriptionIntervalSec);
    if (u.rateLimitIntervalSec !== undefined) d.rateLimitIntervalSec = clampNum(u.rateLimitIntervalSec, 120, 86400, d.rateLimitIntervalSec);
    if (u.costIntervalSec !== undefined) d.costIntervalSec = clampNum(u.costIntervalSec, 120, 86400, d.costIntervalSec);
    if (typeof u.probeModel === 'string') d.probeModel = u.probeModel.trim() || DEFAULT_CONFIG.probeModel;
    if (u.monthlyBudgetUsd !== undefined) d.monthlyBudgetUsd = clampNum(u.monthlyBudgetUsd, 0, 1e9, 0);
    if (typeof u.notifications === 'boolean') d.notifications = u.notifications;
    if (typeof u.launchAtLogin === 'boolean') d.launchAtLogin = u.launchAtLogin;
    if (typeof u.menuBarText === 'boolean') d.menuBarText = u.menuBarText;
    if (typeof u.proxy === 'string') d.proxy = u.proxy.trim() || 'auto';
    for (const k of ['apiKey', 'adminKey', 'oauthToken'] as const) {
      const v = u[k];
      if (v === undefined) continue;
      if (v.trim()) d.secrets[k] = this.encrypt(v.trim());
      else delete d.secrets[k];
    }
    await this.save();
    return this.public();
  }
}
