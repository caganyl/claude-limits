import { contextBridge, ipcRenderer } from 'electron';
import type { PublicSettings, SettingsUpdate, SnapshotMessage, TrayBridge } from './shared';

const bridge: TrayBridge = {
  onSnapshot: (cb) => {
    ipcRenderer.on('snapshot', (_e, m: SnapshotMessage) => cb(m));
  },
  onShowSettings: (cb) => {
    ipcRenderer.on('show-settings', () => cb());
  },
  refresh: () => ipcRenderer.invoke('refresh'),
  getSettings: () => ipcRenderer.invoke('get-settings') as Promise<PublicSettings>,
  saveSettings: (u: SettingsUpdate) => ipcRenderer.invoke('save-settings', u) as Promise<PublicSettings>,
  openUsagePage: () => ipcRenderer.invoke('open-usage'),
  resize: (h) => ipcRenderer.send('resize', h),
  hide: () => ipcRenderer.send('hide'),
  quit: () => ipcRenderer.send('quit'),
};

contextBridge.exposeInMainWorld('claudeLimits', bridge);
