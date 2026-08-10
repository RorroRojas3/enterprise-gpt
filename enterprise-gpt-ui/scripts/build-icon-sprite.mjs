#!/usr/bin/env node
// Builds public/icons/sprite.svg from the checked-in manifest and the individual
// SVGs in bootstrap-icons. US-109.
//
// The full icon font is deliberately not shipped: its stylesheet alone is roughly
// 72 kB against an initial-bundle budget with under 20 kB of headroom, and it would
// carry 2,078 glyphs to render the 74 the design uses.
//
// The sprite is an asset rather than a bundled string. Inlining it through the
// build (`loader: {".svg": "text"}`) would put ~34 kB into the initial JS chunk;
// as a file in public/ it costs zero against the budget and is fetched once.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const MANIFEST = join(UI_ROOT, 'src', 'app', 'shared', 'icon', 'icon-names.ts');
const ICON_DIR = join(UI_ROOT, 'node_modules', 'bootstrap-icons', 'icons');
const OUT_FILE = join(UI_ROOT, 'public', 'icons', 'sprite.svg');

function die(...lines) {
  console.error('\nIcon sprite build FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

// The manifest is read as text rather than imported. Node can strip types today,
// but importing would couple this script to a Node version and to the
// erasable-syntax rules, for no gain over one unambiguous regex.
if (!existsSync(MANIFEST)) die('Icon manifest not found.', `expected: ${MANIFEST}`);
const manifestSource = readFileSync(MANIFEST, 'utf8');
const declaration = manifestSource.match(/ICON_NAMES\s*=\s*\[([\s\S]*?)\]\s*as const/);
if (!declaration) {
  die('Could not find `ICON_NAMES = [ … ] as const` in the manifest.', `file: ${MANIFEST}`);
}
const names = [...declaration[1].matchAll(/'([^']+)'/g)].map((match) => match[1]);

if (names.length === 0) die('The manifest is empty.', `file: ${MANIFEST}`);

const malformed = names.filter((name) => !/^bi-[a-z0-9]+(-[a-z0-9]+)*$/.test(name));
if (malformed.length > 0) {
  die('These manifest entries are not valid glyph names:', ...malformed.map((n) => `  ${n}`));
}

const duplicated = names.filter((name, index) => names.indexOf(name) !== index);
if (duplicated.length > 0) {
  die('The manifest contains duplicates:', ...[...new Set(duplicated)].map((n) => `  ${n}`));
}

const sorted = [...names].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
const outOfOrder = names.findIndex((name, index) => name !== sorted[index]);
if (outOfOrder >= 0) {
  die(
    'The manifest is not sorted, which makes its diffs unreadable.',
    `first out of order: ${names[outOfOrder]} (expected ${sorted[outOfOrder]})`,
  );
}

const symbols = [];
const missing = [];

for (const name of names) {
  const file = join(ICON_DIR, `${name.slice('bi-'.length)}.svg`);
  if (!existsSync(file)) {
    missing.push([name, file]);
    continue;
  }

  const source = readFileSync(file, 'utf8');
  const root = source.match(/<svg\b([^>]*)>([\s\S]*)<\/svg>/);
  if (!root) die(`Unrecognised SVG shape: ${file}`);

  const viewBox = root[1].match(/viewBox="([^"]+)"/)?.[1];
  if (!viewBox) die(`Source SVG has no viewBox: ${file}`);

  // `fill` moves onto the <symbol> because the source carried it on the <svg> root
  // that is being discarded. `currentColor` is what lets one sprite serve both
  // themes, and it only resolves that way because the sprite is injected into the
  // host document rather than referenced as an external file.
  const body = root[2].replace(/\s+/g, ' ').trim();
  symbols.push(`<symbol id="${name}" viewBox="${viewBox}" fill="currentColor">${body}</symbol>`);
}

if (missing.length > 0) {
  die(
    'These glyphs are in the manifest but not in bootstrap-icons:',
    ...missing.map(([name, file]) => `  ${name}  (looked for ${file})`),
    '',
    'Check the spelling against https://icons.getbootstrap.com, or remove the entry.',
  );
}

// position/overflow rather than display:none — hidden sprites have a long history
// of engine bugs with the latter, and this is the convention every sprite tool uses.
const sprite =
  '<svg xmlns="http://www.w3.org/2000/svg" aria-hidden="true" focusable="false" ' +
  'style="position:absolute;width:0;height:0;overflow:hidden">' +
  symbols.join('') +
  '</svg>\n';

mkdirSync(join(UI_ROOT, 'public', 'icons'), { recursive: true });
writeFileSync(OUT_FILE, sprite, 'utf8');

console.log(
  `icon sprite OK — ${symbols.length} glyphs, ${(sprite.length / 1024).toFixed(1)} kB → public/icons/sprite.svg`,
);
