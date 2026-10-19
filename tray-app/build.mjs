import * as esbuild from 'esbuild';
import { cpSync, mkdirSync } from 'node:fs';

const common = { bundle: true, logLevel: 'warning', sourcemap: false, target: 'es2022' };

await Promise.all([
  esbuild.build({ ...common, entryPoints: ['src/main.ts'], outfile: 'dist/main.js', platform: 'node', format: 'cjs', external: ['electron'] }),
  esbuild.build({ ...common, entryPoints: ['src/preload.ts'], outfile: 'dist/preload.js', platform: 'node', format: 'cjs', external: ['electron'] }),
  esbuild.build({ ...common, entryPoints: ['src/renderer.ts'], outfile: 'dist/renderer.js', platform: 'browser', format: 'iife' }),
  // Exposed for tests (icon rendering without Electron).
  esbuild.build({ ...common, entryPoints: ['src/icon.ts'], outfile: 'dist/icon.js', platform: 'node', format: 'cjs' }),
]);
mkdirSync('dist', { recursive: true });
cpSync('src/popup.html', 'dist/popup.html');
console.log('tray-app built');
