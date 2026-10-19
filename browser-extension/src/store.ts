// chrome.storage.local layout shared by background, popup and options.
import type { DisplayMode } from '../../core/src/format';
import type { Level, Snapshot } from '../../core/src/model';

export interface ExtSettings {
  displayMode: DisplayMode;
  warn: number;
  crit: number;
  subscriptionIntervalMin: number;
  rateLimitIntervalMin: number;
  costIntervalMin: number;
  probeModel: string;
  monthlyBudgetUsd: number;
  notifications: boolean;
  apiKey: string;
  adminKey: string;
}

export const DEFAULT_SETTINGS: ExtSettings = {
  displayMode: 'session',
  warn: 75,
  crit: 90,
  subscriptionIntervalMin: 3,
  rateLimitIntervalMin: 15,
  costIntervalMin: 10,
  probeModel: 'claude-haiku-5-5',
  monthlyBudgetUsd: 0,
  notifications: true,
  apiKey: '',
  adminKey: '',
};

export interface ExtState {
  snapshot?: Snapshot;
  /** Epoch ms of the last attempt per section. */
  lastRun?: { subscription?: number; rateLimits?: number; cost?: number };
  /** Epoch ms before which a section must not be retried (HTTP 429). */
  backoff?: { subscription?: number; rateLimits?: number; cost?: number };
  notifyState?: Record<string, Level>;
}

export async function getSettings(): Promise<ExtSettings> {
  const { settings } = await chrome.storage.local.get('settings');
  return { ...DEFAULT_SETTINGS, ...((settings as Partial<ExtSettings>) ?? {}) };
}

export async function saveSettings(s: Partial<ExtSettings>): Promise<ExtSettings> {
  const next = { ...(await getSettings()), ...s };
  await chrome.storage.local.set({ settings: next });
  return next;
}

export async function getState(): Promise<ExtState> {
  const { state } = await chrome.storage.local.get('state');
  return (state as ExtState) ?? {};
}

export async function saveState(s: ExtState): Promise<void> {
  await chrome.storage.local.set({ state: s });
}
