// Draws the tray icons in pure JS (no native canvas): a rounded tile coloured by level with the
// percentage as digits (Windows), and a monochrome gauge template image (macOS menu bar).
import { deflateSync } from 'node:zlib';
import type { Level } from '../../core/src/model';

// ---------------------------------------------------------------- PNG encoder
const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf: Buffer): number {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}

/** Encodes straight (non-premultiplied) RGBA pixels as PNG. */
export function encodePng(width: number, height: number, rgba: Uint8Array): Buffer {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0;
    Buffer.from(rgba.buffer, rgba.byteOffset + y * width * 4, width * 4).copy(raw, y * (width * 4 + 1) + 1);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---------------------------------------------------------------- tiny raster with supersampling
type RGBA = [number, number, number, number];

class Canvas {
  readonly px: Float32Array; // premultiplied RGBA 0..1
  constructor(readonly w: number, readonly h: number) {
    this.px = new Float32Array(w * h * 4);
  }
  /** Paints colour with coverage computed by `inside(x, y)` sampled ss×ss per pixel. */
  fill(color: RGBA, inside: (x: number, y: number) => boolean, ss = 4): void {
    const [r, g, b, a] = color.map((v, i) => (i < 3 ? v / 255 : v)) as RGBA;
    for (let py = 0; py < this.h; py++) {
      for (let px = 0; px < this.w; px++) {
        let hits = 0;
        for (let sy = 0; sy < ss; sy++)
          for (let sx = 0; sx < ss; sx++) if (inside(px + (sx + 0.5) / ss, py + (sy + 0.5) / ss)) hits++;
        if (!hits) continue;
        const cov = (hits / (ss * ss)) * a;
        const i = (py * this.w + px) * 4;
        this.px[i] = r * cov + this.px[i] * (1 - cov);
        this.px[i + 1] = g * cov + this.px[i + 1] * (1 - cov);
        this.px[i + 2] = b * cov + this.px[i + 2] * (1 - cov);
        this.px[i + 3] = cov + this.px[i + 3] * (1 - cov);
      }
    }
  }
  /** Erases (punches out) where `inside` is true — used for knocked-out digits. */
  png(): Buffer {
    const out = new Uint8Array(this.w * this.h * 4);
    for (let i = 0; i < this.w * this.h; i++) {
      const a = this.px[i * 4 + 3];
      out[i * 4 + 3] = Math.round(a * 255);
      if (a > 0) for (let c = 0; c < 3; c++) out[i * 4 + c] = Math.round(Math.min(1, this.px[i * 4 + c] / a) * 255);
    }
    return encodePng(this.w, this.h, out);
  }
}

function roundedRect(x0: number, y0: number, x1: number, y1: number, r: number) {
  return (x: number, y: number) => {
    if (x < x0 || x > x1 || y < y0 || y > y1) return false;
    const cx = Math.min(Math.max(x, x0 + r), x1 - r);
    const cy = Math.min(Math.max(y, y0 + r), y1 - r);
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
  };
}

// 5×7 bitmap digits (and a few symbols). Each row is 5 bits, MSB = left.
const GLYPHS: Record<string, number[]> = {
  '0': [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
  '1': [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
  '2': [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
  '3': [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
  '4': [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
  '5': [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
  '6': [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
  '7': [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
  '8': [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
  '9': [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
  '!': [0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100],
  '-': [0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000],
  '?': [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100],
};

function textCoverage(text: string, size: number) {
  // Scale so 2 glyphs (5 wide + 1 gap) fit with a margin; pixel-snapped for crispness.
  const scale = Math.max(1, Math.floor((size * 0.78) / (text.length * 6 - 1)));
  const cell = Math.min(scale, Math.floor((size * 0.7) / 7)) || 1;
  const w = (text.length * 6 - 1) * cell;
  const h = 7 * cell;
  const ox = Math.round((size - w) / 2);
  const oy = Math.round((size - h) / 2);
  return (x: number, y: number) => {
    const gx = Math.floor((x - ox) / cell);
    const gy = Math.floor((y - oy) / cell);
    if (gy < 0 || gy >= 7 || gx < 0) return false;
    const idx = Math.floor(gx / 6);
    const col = gx % 6;
    if (idx >= text.length || col === 5) return false;
    const g = GLYPHS[text[idx]];
    return !!g && ((g[gy] >> (4 - col)) & 1) === 1;
  };
}

const LEVEL_COLORS: Record<Level, RGBA> = {
  ok: [74, 138, 92, 1],
  warn: [214, 140, 30, 1],
  crit: [205, 64, 45, 1],
  unknown: [120, 116, 108, 1],
};

/** Text that fits on the tile: "0".."99", "!" at 100 %, "-" when unknown, "?" on error. */
export function iconText(percent: number | undefined, error = false): string {
  if (percent === undefined || !Number.isFinite(percent)) return error ? '?' : '-';
  const p = Math.round(percent);
  return p >= 100 ? '!' : String(Math.max(0, p));
}

/** Windows/Linux tray icon: coloured tile with white digits. */
export function percentIconPng(size: number, percent: number | undefined, level: Level, error = false): Buffer {
  const c = new Canvas(size, size);
  const r = size * 0.22;
  c.fill(LEVEL_COLORS[level], roundedRect(0, 0, size, size, r));
  c.fill([255, 255, 255, 1], textCoverage(iconText(percent, error), size), 1);
  return c.png();
}

/** macOS template image: black gauge on transparent; the system recolours it. */
export function gaugeTemplatePng(size: number, percent = 62): Buffer {
  const c = new Canvas(size, size);
  const cx = size / 2;
  const cy = size * 0.62;
  const R = size * 0.42;
  const w = size * 0.13;
  const frac = Math.max(0, Math.min(1, percent / 100));
  const ring = (filled: boolean) => (x: number, y: number) => {
    const dx = x - cx;
    const dy = y - cy;
    const d = Math.hypot(dx, dy);
    if (d > R || d < R - w || dy > 0.5) return false;
    const ang = Math.atan2(-dy, -dx); // 0 at left, π at right
    const t = ang / Math.PI;
    return filled ? t <= frac : t > frac;
  };
  c.fill([0, 0, 0, 0.35], ring(false));
  c.fill([0, 0, 0, 1], ring(true));
  const a = Math.PI * frac;
  const nx = cx - Math.cos(a) * R * 0.72;
  const ny = cy - Math.sin(a) * R * 0.72;
  const nw = size * 0.075;
  c.fill([0, 0, 0, 1], (x, y) => {
    const vx = nx - cx;
    const vy = ny - cy;
    const t = Math.max(0, Math.min(1, ((x - cx) * vx + (y - cy) * vy) / (vx * vx + vy * vy)));
    return Math.hypot(x - (cx + t * vx), y - (cy + t * vy)) <= nw;
  });
  c.fill([0, 0, 0, 1], (x, y) => Math.hypot(x - cx, y - cy) <= size * 0.1);
  return c.png();
}
