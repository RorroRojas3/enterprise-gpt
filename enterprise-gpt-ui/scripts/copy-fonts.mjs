#!/usr/bin/env node
// Copies the exact self-hosted font faces US-109 names out of node_modules into
// public/fonts. FR-51 forbids any runtime request to a third-party origin, so
// Google Fonts is not an option and the files have to ship from our own origin.
//
// A script rather than an `angular.json` asset glob for three reasons: the AC says
// the faces are served *from public/fonts*, a glob leaves that directory empty; a
// glob that matches nothing after an upstream rename ships zero fonts silently;
// and the sprite build needs a script regardless, so one mechanism is cheaper than
// two.
import { copyFileSync, existsSync, mkdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const OUT_DIR = join(UI_ROOT, 'public', 'fonts');

/**
 * `latin-ext` is carried for Inter alone. Inter renders user display names and
 * model output, where an accented character is a matter of time; Montserrat and
 * JetBrains Mono only ever render app copy and machine values. `unicode-range` in
 * _typography.scss means the extra files cost nothing until such a character
 * actually appears.
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
