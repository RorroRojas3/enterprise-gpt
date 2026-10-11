#!/usr/bin/env node
// Self-hosts the font faces in public/fonts, since no runtime request may reach a third-party
// origin. A script rather than an asset glob, so an upstream rename fails instead of shipping none.
import { copyFileSync, existsSync, mkdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const OUT_DIR = join(UI_ROOT, 'public', 'fonts');

/**
 * Only Inter carries `latin-ext`: it renders user names and model output. `unicode-range` in
 * _typography.scss keeps those files unloaded until an accented character appears.
 */
const FAMILIES = [
  {
    pkg: '@fontsource/inter',
    family: 'inter',
    subsets: ['latin', 'latin-ext'],
    weights: [400, 500, 600],
  },
  {
    pkg: '@fontsource/montserrat',
    family: 'montserrat',
    subsets: ['latin'],
    weights: [600, 700, 800],
  },
  {
    pkg: '@fontsource/jetbrains-mono',
    family: 'jetbrains-mono',
    subsets: ['latin'],
    weights: [400, 500, 600],
  },
];

const missing = [];
const copied = [];

mkdirSync(OUT_DIR, { recursive: true });

for (const { pkg, family, subsets, weights } of FAMILIES) {
  for (const subset of subsets) {
    for (const weight of weights) {
      const name = `${family}-${subset}-${weight}-normal.woff2`;
      const source = join(UI_ROOT, 'node_modules', ...pkg.split('/'), 'files', name);
      if (!existsSync(source)) {
        missing.push(source);
        continue;
      }
      copyFileSync(source, join(OUT_DIR, name));
      copied.push([name, statSync(source).size]);
    }
  }
}

if (missing.length > 0) {
  console.error('\nFont copy FAILED — these faces are not in node_modules:\n');
  for (const path of missing) console.error('  ' + path);
  console.error('\nRun `npm ci`, or check whether @fontsource renamed the file.\n');
  process.exit(1);
}

const total = copied.reduce((sum, [, size]) => sum + size, 0);
console.log(
  `fonts OK — ${copied.length} woff2 copied into public/fonts (${(total / 1024).toFixed(1)} kB)`,
);
