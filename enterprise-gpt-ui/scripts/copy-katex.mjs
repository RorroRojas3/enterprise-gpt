#!/usr/bin/env node
// Copies KaTeX's stylesheet and font faces out of node_modules into
// public/vendor/katex, for US-605's math flag.
//
// Self-hosted because FR-51 forbids any runtime request to a third-party origin,
// and copied rather than bundled because the stylesheet has to stay *out* of the
// build: `styles.scss` is one eagerly-loaded bundle with ~2 kB of headroom under
// its budget, and math is a flag most deployments leave off. The loader injects a
// `<link>` at the moment the first expression appears instead.
//
// A script rather than an `angular.json` asset glob, for the reason copy-fonts.mjs
// gives: a glob that matches nothing after an upstream rename ships zero files and
// says nothing, and the failure would surface as unstyled math in production.
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

// woff2 only. KaTeX declares each face three times — woff2, woff, ttf — with a
// `format()` hint on each, so a browser picks the first it supports and never
// requests the other two. Shipping all three would quadruple the directory for
// formats nothing this app supports would ever ask for.
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
