#!/usr/bin/env node
// Fails on a `bi-` glyph missing from the sprite manifest, covering what `strictTemplates` cannot:
// a raw `class="bi bi-…"` or a `bi-*` literal bound as a class. `Icon` logs run-time names in dev.
import { readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { stripComments, walk } from './lib/source-scan.mjs';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const SRC = join(UI_ROOT, 'src');
const MANIFEST = join(SRC, 'app', 'shared', 'utils', 'icons', 'icon-names.ts');
const MANIFEST_REL = 'app/shared/utils/icons/icon-names.ts';

/** Structural Bootstrap-Icons classes that are not glyph names. */
const NOT_GLYPHS = new Set(['bi']);

/** Opt out of one line, for the rare case the scan is wrong. */
const ESCAPE = 'icon-check-ignore-next-line';

function die(...lines) {
  console.error('\nIcon name check FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

let manifestSource;
try {
  manifestSource = readFileSync(MANIFEST, 'utf8');
} catch {
  die(`Icon manifest not found: ${MANIFEST}.`, 'This check cannot run without it.');
}

const declaration = manifestSource.match(/ICON_NAMES\s*=\s*\[([\s\S]*?)\]\s*as const/);
if (!declaration) die('Could not find `ICON_NAMES = [ … ] as const`.', `file: ${MANIFEST}`);
const known = new Set([...declaration[1].matchAll(/'([^']+)'/g)].map((match) => match[1]));

const unknown = [];

for (const file of walk(SRC)) {
  const rel = relative(SRC, file).replaceAll('\\', '/');
  if (rel === MANIFEST_REL) continue;

  const isHtml = rel.endsWith('.html');
  if (!isHtml && !rel.endsWith('.ts')) continue;

  const source = readFileSync(file, 'utf8');
  const lines = stripComments(source, { html: isHtml }).split('\n');
  const raw = source.split('\n');

  lines.forEach((line, index) => {
    if ((raw[index - 1] ?? '').includes(ESCAPE)) return;
    for (const [name] of line.matchAll(/\bbi-[a-z0-9][a-z0-9-]*\b/g)) {
      if (!NOT_GLYPHS.has(name) && !known.has(name)) {
        unknown.push([name, `src/${rel}:${index + 1}`]);
      }
    }
  });
}

if (unknown.length > 0) {
  die(
    ...unknown.flatMap(([name, where]) => [
      `Unknown icon "${name}" — ${where}`,
      `  It is not in src/${MANIFEST_REL}, so it is not in the sprite and renders blank.`,
      '  Add it to the manifest and re-run `npm run assets:icons`, or fix the spelling.',
      '',
    ]),
  );
}

console.log(
  `icon names OK — every bi-* reference under src/ is one of ${known.size} sprite glyphs`,
);
