// Renders a sheet of tray icons so they can be checked visually, and validates the PNG bytes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { percentIconPng, gaugeTemplatePng, iconText, encodePng } = require('../dist/icon.js');

assert.equal(iconText(undefined), '-');
assert.equal(iconText(undefined, true), '?');
assert.equal(iconText(42.4), '42');
assert.equal(iconText(100), '!');
assert.equal(iconText(-3), '0');

const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const one = encodePng(1, 1, new Uint8Array([255, 0, 0, 255]));
assert.ok(one.subarray(0, 8).equals(sig));

const out = path.join(__dirname, '..', 'dist', 'icon-samples');
fs.mkdirSync(out, { recursive: true });
const cases = [
  [5, 'ok'], [42, 'ok'], [78, 'warn'], [93, 'crit'], [100, 'crit'], [undefined, 'unknown'],
];
for (const size of [16, 32]) {
  for (const [p, lv] of cases) {
    const png = percentIconPng(size, p, lv);
    assert.ok(png.subarray(0, 8).equals(sig));
    fs.writeFileSync(path.join(out, `pct-${size}-${p ?? 'na'}.png`), png);
  }
  fs.writeFileSync(path.join(out, `gauge-${size}.png`), gaugeTemplatePng(size));
}
console.log('ICON OK →', out);
