#!/usr/bin/env node
// Self-hosts KaTeX outside the build: styles.scss has ~2 kB of budget headroom and math is
// usually off, so the loader links the stylesheet on first use. A script, as in copy-fonts.mjs.
import { copyFileSync, existsSync, mkdirSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const SOURCE = join(UI_ROOT, 'node_modules', 'katex', 'dist');
const OUT_DIR = join(UI_ROOT, 'public', 'vendor', 'katex');
const FONTS_OUT = join(OUT_DIR, 'fonts');

const STYLESHEET = 'katex.min.css';

function die(...lines) {
  console.error('\nKaTeX copy FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('\nRun `npm ci`, or check whether KaTeX moved its dist layout.\n');
  process.exit(1);
}

if (!existsSync(join(SOURCE, STYLESHEET))) {
  die(`Not in node_modules: ${join(SOURCE, STYLESHEET)}`);
}

const fontSource = join(SOURCE, 'fonts');
if (!existsSync(fontSource)) {
  die(`Not in node_modules: ${fontSource}`);
}

// woff2 only: KaTeX lists it first with a `format()` hint, so supported browsers never request
// the woff or ttf fallbacks.
const faces = readdirSync(fontSource).filter((name) => name.endsWith('.woff2'));
if (faces.length === 0) {
  die(`No .woff2 faces under ${fontSource}`);
}

mkdirSync(FONTS_OUT, { recursive: true });
copyFileSync(join(SOURCE, STYLESHEET), join(OUT_DIR, STYLESHEET));

let total = statSync(join(SOURCE, STYLESHEET)).size;
for (const face of faces) {
  copyFileSync(join(fontSource, face), join(FONTS_OUT, face));
  total += statSync(join(fontSource, face)).size;
}

console.log(
  `katex OK — ${STYLESHEET} and ${faces.length} woff2 faces copied into ` +
    `public/vendor/katex (${(total / 1024).toFixed(1)} kB)`,
);
