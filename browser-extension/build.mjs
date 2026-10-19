// Builds the unpacked extension into build/ and zips it for the Chrome Web Store / Edge Add-ons.
import * as esbuild from 'esbuild';
import { cpSync, mkdirSync, rmSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

rmSync('build', { recursive: true, force: true });
mkdirSync('build', { recursive: true });

await esbuild.build({
  entryPoints: { background: 'src/background.ts', popup: 'src/popup.ts', options: 'src/options.ts' },
  outdir: 'build',
  bundle: true,
  format: 'iife',
  platform: 'browser',
  target: 'chrome116',
  logLevel: 'warning',
});

cpSync('src/manifest.json', 'build/manifest.json');
cpSync('src/popup.html', 'build/popup.html');
cpSync('src/options.html', 'build/options.html');
cpSync('icons', 'build/icons', { recursive: true });

const { version } = JSON.parse(readFileSync('src/manifest.json', 'utf8'));
const zip = `claude-limits-browser-${version}.zip`;
rmSync(zip, { force: true });
try {
  execFileSync('zip', ['-qr', `../${zip}`, '.'], { cwd: 'build' });
  console.log(`browser extension built → build/ and ${zip}`);
} catch {
  console.log('browser extension built → build/ (zip tool not found; load build/ as unpacked)');
}
