// Claude Limits tray app — Windows (notification area) and macOS (menu bar).
import { BrowserWindow, Menu, Notification, Tray, app, ipcMain, nativeImage, nativeTheme, screen, shell } from 'electron';
import * as path from 'node:path';
import { promises as fs } from 'node:fs';
import { compactText, formatPercent, formatResetIn, headlinePercent, snapshotLevel } from '../../core/src/format';
import type { Snapshot } from '../../core/src/model';
import { ThresholdNotifier } from '../../core/src/notify';
import { LimitsService } from '../../core/src/service';
import { gaugeTemplatePng, percentIconPng } from './icon';
import { SettingsStore } from './settings';
import type { SettingsUpdate, SnapshotMessage } from './shared';

const isMac = process.platform === 'darwin';
const POPUP_WIDTH = 360;
const SCREENSHOT = process.env.CLAUDE_LIMITS_SCREENSHOT; // test hook: save a PNG of the popup and quit

let tray: Tray | undefined;
let popup: BrowserWindow | undefined;
let service: LimitsService;
let lastHiddenAt = 0;
const settings = new SettingsStore();
const notifier = new ThresholdNotifier();

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => showPopup());
  app.whenReady().then(start).catch((e) => {
    console.error(e);
    app.quit();
  });
}

// A tray app keeps running with no windows open.
app.on('window-all-closed', () => {
  /* stay alive */
});

function start(): void {
  if (isMac) app.dock?.hide();
  if (process.platform === 'win32') app.setAppUserModelId('com.caganyl.claudelimits');
  settings.load();
  applyLoginItem();

  tray = new Tray(initialIcon());
  tray.setToolTip('Claude Limits — yükleniyor…');
  if (isMac) tray.setIgnoreDoubleClickEvents(true);
  tray.on('click', () => togglePopup());
  tray.on('right-click', () => tray?.popUpContextMenu(contextMenu()));

  createPopup();
  registerIpc();

  service = new LimitsService(() => settings.serviceConfig(), onSnapshot);
  service.start(30);
  // Keep countdowns fresh in the tooltip/popup between fetches.
  setInterval(() => onSnapshot(service.snapshot, false), 60_000);
}

function initialIcon(): Electron.NativeImage {
  if (isMac) {
    const img = nativeImage.createFromBuffer(gaugeTemplatePng(16), { scaleFactor: 1 });
    img.addRepresentation({ scaleFactor: 2, buffer: gaugeTemplatePng(32) });
    img.setTemplateImage(true);
    return img;
  }
  return percentImage(undefined, 'unknown', false);
}

function percentImage(p: number | undefined, level: ReturnType<typeof snapshotLevel>, error: boolean): Electron.NativeImage {
  const img = nativeImage.createFromBuffer(percentIconPng(16, p, level, error), { scaleFactor: 1 });
  img.addRepresentation({ scaleFactor: 1.5, buffer: percentIconPng(24, p, level, error) });
  img.addRepresentation({ scaleFactor: 2, buffer: percentIconPng(32, p, level, error) });
  return img;
}

function onSnapshot(s: Snapshot, notify = true): void {
  if (!tray) return;
  const t = settings.thresholds;
  const mode = settings.displayMode;
  const level = snapshotLevel(s, t);
  const text = compactText(s, mode);
  const hasError = !!(s.subscriptionError || s.rateLimitsError || s.costError);

  if (isMac) {
    tray.setTitle(settings.menuBarText ? (text ? ` ${text}` : '') : '', { fontType: 'monospacedDigit' });
  } else {
    tray.setImage(percentImage(headlinePercent(s, mode), level, hasError && !s.subscription && !s.rateLimits));
  }
  tray.setToolTip(tooltip(s).slice(0, 127));

  if (popup && !popup.isDestroyed()) popup.webContents.send('snapshot', message(s));

  if (notify && settings.notifications && Notification.isSupported()) {
    for (const a of notifier.check(s, t)) {
      const n = new Notification({ title: 'Claude Limits', body: a.message, silent: a.level !== 'crit' });
      n.on('click', () => showPopup());
      n.show();
    }
  }

  if (SCREENSHOT && notify && (s.subscription || s.subscriptionError)) void screenshotAndQuit(SCREENSHOT);
}

function tooltip(s: Snapshot): string {
  const lines = ['Claude Limits'];
  for (const w of s.subscription?.windows.slice(0, 3) ?? []) {
    const reset = formatResetIn(w.resetsAt);
    lines.push(`${w.shortLabel} ${formatPercent(w.percent)}${reset ? ` (${reset})` : ''}`);
  }
  if (!s.subscription && s.subscriptionError) lines.push('Abonelik: hata');
  if (s.rateLimits?.buckets.length) {
    lines.push(`API ${formatPercent(Math.max(...s.rateLimits.buckets.map((b) => b.percentUsed)))}`);
  }
  return lines.join('\n');
}

function message(s: Snapshot): SnapshotMessage {
  const pub = settings.public();
  return { snapshot: s, thresholds: settings.thresholds, hasApiKey: pub.hasApiKey, hasAdminKey: pub.hasAdminKey };
}

// ------------------------------------------------------------------ popup
function createPopup(): void {
  popup = new BrowserWindow({
    width: POPUP_WIDTH,
    height: 480,
    show: false,
    frame: false,
    resizable: false,
    movable: false,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    transparent: false,
    backgroundColor: nativeTheme.shouldUseDarkColors ? '#1c1b19' : '#f7f6f3',
    roundedCorners: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });
  popup.setMenu(null);
  void popup.loadFile(path.join(__dirname, 'popup.html'));
  popup.webContents.on('did-finish-load', () => popup?.webContents.send('snapshot', message(service?.snapshot ?? { updatedAt: '' })));
  // Links never open inside the popup.
  popup.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  popup.webContents.on('will-navigate', (e) => e.preventDefault());
  popup.on('blur', () => {
    if (!SCREENSHOT && popup?.isVisible() && !popup.webContents.isDevToolsOpened()) {
      popup.hide();
      lastHiddenAt = Date.now();
    }
  });
}

function togglePopup(): void {
  if (!popup) return;
  if (popup.isVisible()) {
    popup.hide();
    return;
  }
  // Clicking the tray icon while the popup is open first blurs it (hiding it); don't reopen at once.
  if (Date.now() - lastHiddenAt < 250) return;
  showPopup();
}

function showPopup(): void {
  if (!popup) return;
  positionPopup();
  popup.show();
  popup.focus();
  void service?.refresh(false);
}

function positionPopup(): void {
  if (!popup || !tray) return;
  const tb = tray.getBounds();
  const wb = popup.getBounds();
  const anchor = tb.width > 0 ? { x: tb.x + tb.width / 2, y: tb.y + tb.height / 2 } : screen.getCursorScreenPoint();
  const wa = screen.getDisplayNearestPoint({ x: Math.round(anchor.x), y: Math.round(anchor.y) }).workArea;
  const margin = 8;
  let x = Math.round(anchor.x - wb.width / 2);
  let y: number;
  if (isMac) {
    y = tb.height > 0 ? tb.y + tb.height + 4 : wa.y + margin;
  } else if (anchor.y < wa.y + wa.height / 2) {
    y = (tb.height > 0 ? tb.y + tb.height : wa.y) + margin; // taskbar at the top
  } else {
    y = (tb.height > 0 ? tb.y : wa.y + wa.height) - wb.height - margin; // taskbar at the bottom (default)
  }
  if (anchor.x > wa.x + wa.width) x = wa.x + wa.width - wb.width - margin; // taskbar on the right
  if (anchor.x < wa.x) x = wa.x + margin; // taskbar on the left
  x = Math.min(Math.max(x, wa.x + margin), wa.x + wa.width - wb.width - margin);
  y = Math.min(Math.max(y, wa.y + margin), wa.y + wa.height - wb.height - margin);
  popup.setPosition(x, y, false);
}

function contextMenu(): Menu {
  return Menu.buildFromTemplate([
    { label: 'Limitleri göster', click: () => showPopup() },
    { label: 'Şimdi yenile', click: () => void service.refresh(true) },
    {
      label: 'Ayarlar…',
      click: () => {
        showPopup();
        popup?.webContents.send('show-settings');
      },
    },
    { label: 'claude.ai kullanım sayfası', click: () => void shell.openExternal('https://claude.ai/settings/usage') },
    { type: 'separator' },
    {
      label: 'Oturum açılışında başlat',
      type: 'checkbox',
      checked: settings.launchAtLogin,
      click: async (item) => {
        await settings.update({ launchAtLogin: item.checked });
        applyLoginItem();
      },
    },
    { type: 'separator' },
    { label: `Claude Limits ${app.getVersion()}`, enabled: false },
    { label: 'Çıkış', role: 'quit' },
  ]);
}

function applyLoginItem(): void {
  if (!app.isPackaged) return; // don't register the dev Electron binary
  try {
    app.setLoginItemSettings({ openAtLogin: settings.launchAtLogin });
  } catch {
    /* unsupported platform */
  }
}

// ------------------------------------------------------------------ IPC
function registerIpc(): void {
  const fromPopup = (e: Electron.IpcMainEvent | Electron.IpcMainInvokeEvent) => !!popup && e.sender === popup.webContents;

  ipcMain.handle('refresh', async (e) => {
    if (!fromPopup(e)) return;
    await service.refresh(true);
  });
  ipcMain.handle('get-settings', (e) => (fromPopup(e) ? settings.public() : undefined));
  ipcMain.handle('save-settings', async (e, u: SettingsUpdate) => {
    if (!fromPopup(e)) return undefined;
    const pub = await settings.update(u ?? {});
    applyLoginItem();
    await service.refresh(true);
    return pub;
  });
  ipcMain.handle('open-usage', (e) => {
    if (fromPopup(e)) void shell.openExternal('https://claude.ai/settings/usage');
  });
  ipcMain.on('resize', (e, height: number) => {
    if (!fromPopup(e) || !popup) return;
    const h = Math.max(160, Math.min(640, Math.ceil(Number(height) || 0)));
    const [w, cur] = popup.getContentSize();
    if (Math.abs(cur - h) > 2) {
      popup.setContentSize(w, h);
      if (popup.isVisible()) positionPopup();
    }
  });
  ipcMain.on('hide', (e) => {
    if (fromPopup(e)) popup?.hide();
  });
  ipcMain.on('quit', (e) => {
    if (fromPopup(e)) app.quit();
  });
}

// ------------------------------------------------------------------ test hook
let shotTaken = false;
async function screenshotAndQuit(file: string): Promise<void> {
  if (shotTaken || !popup) return;
  shotTaken = true;
  if (process.env.CLAUDE_LIMITS_SCREENSHOT_SETTINGS) popup.webContents.send('show-settings');
  popup.setPosition(40, 40);
  popup.show();
  await new Promise((r) => setTimeout(r, 1500));
  const img = await popup.webContents.capturePage();
  await fs.writeFile(file, img.toPNG());
  if (tray) {
    // Also dump the current tray icon for inspection.
    const icon = percentIconPng(32, headlinePercent(service.snapshot, settings.displayMode), snapshotLevel(service.snapshot, settings.thresholds));
    await fs.writeFile(file.replace(/\.png$/, '') + '-tray.png', icon);
  }
  app.quit();
}
