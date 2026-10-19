// Types shared by the main process, preload and renderer.
import type { DisplayMode } from '../../core/src/format';
import type { Snapshot, Thresholds } from '../../core/src/model';

export interface PublicSettings {
  subscriptionEnabled: boolean;
  displayMode: DisplayMode;
  warn: number;
  crit: number;
  subscriptionIntervalSec: number;
  rateLimitIntervalSec: number;
  costIntervalSec: number;
  probeModel: string;
  monthlyBudgetUsd: number;
  notifications: boolean;
  launchAtLogin: boolean;
  /** macOS: show the percentages as text next to the menu bar icon. */
  menuBarText: boolean;
  /** "auto" | "direct" | proxy URL. */
  proxy: string;
  hasApiKey: boolean;
  hasAdminKey: boolean;
  hasOAuthToken: boolean;
  /** False when the OS offers no encrypted storage (keys would be stored in plain text). */
  secureStorage: boolean;
  platform: NodeJS.Platform;
  version: string;
}

/** Fields the renderer may send back. Secrets: string = set, "" = clear, undefined = keep. */
export interface SettingsUpdate extends Partial<Omit<PublicSettings, 'hasApiKey' | 'hasAdminKey' | 'hasOAuthToken' | 'secureStorage' | 'platform' | 'version'>> {
  apiKey?: string;
  adminKey?: string;
  oauthToken?: string;
}

export interface SnapshotMessage {
  snapshot: Snapshot;
  thresholds: Thresholds;
  hasApiKey: boolean;
  hasAdminKey: boolean;
}

export interface TrayBridge {
  onSnapshot(cb: (m: SnapshotMessage) => void): void;
  onShowSettings(cb: () => void): void;
  refresh(): Promise<void>;
  getSettings(): Promise<PublicSettings>;
  saveSettings(u: SettingsUpdate): Promise<PublicSettings>;
  openUsagePage(): Promise<void>;
  resize(height: number): void;
  hide(): void;
  quit(): void;
}
