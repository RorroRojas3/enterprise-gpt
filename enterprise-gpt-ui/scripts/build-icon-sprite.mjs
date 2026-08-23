#!/usr/bin/env node
// Builds public/icons/sprite.svg from two checked-in manifests: the monochrome
// glyphs, resolved against the individual SVGs in bootstrap-icons, and the
// multi-colour brand marks, resolved against src/app/shared/icon/brand/.
// US-109, US-418.
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
const ICON_ROOT = join(UI_ROOT, 'src', 'app', 'shared', 'icon');
const MANIFEST = join(ICON_ROOT, 'icon-names.ts');
const ICON_DIR = join(UI_ROOT, 'node_modules', 'bootstrap-icons', 'icons');
const BRAND_MANIFEST = join(ICON_ROOT, 'brand-icon-names.ts');
const BRAND_DIR = join(ICON_ROOT, 'brand');
const OUT_FILE = join(UI_ROOT, 'public', 'icons', 'sprite.svg');

function die(...lines) {
  console.error('\nIcon sprite build FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

// A manifest is read as text rather than imported. Node can strip types today,
// but importing would couple this script to a Node version and to the
// erasable-syntax rules, for no gain over one unambiguous regex.
function readManifest(file, constName, pattern, label) {
  if (!existsSync(file)) die(`${label} manifest not found.`, `expected: ${file}`);

  const source = readFileSync(file, 'utf8');
  const declaration = source.match(
    new RegExp(String.raw`${constName}\s*=\s*\[([\s\S]*?)\]\s*as const`),
  );
  if (!declaration) {
    die(`Could not find \`${constName} = [ … ] as const\` in the manifest.`, `file: ${file}`);
  }

  const names = [...declaration[1].matchAll(/'([^']+)'/g)].map((match) => match[1]);
  if (names.length === 0) die(`The ${label.toLowerCase()} manifest is empty.`, `file: ${file}`);

  const malformed = names.filter((name) => !pattern.test(name));
  if (malformed.length > 0) {
    die(
      `These ${label.toLowerCase()} entries are not valid names:`,
      ...malformed.map((n) => '  ' + n),
    );
  }

  const duplicated = names.filter((name, index) => names.indexOf(name) !== index);
  if (duplicated.length > 0) {
    die(
      `The ${label.toLowerCase()} manifest contains duplicates:`,
      ...[...new Set(duplicated)].map((n) => '  ' + n),
    );
  }

  const sorted = [...names].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  const outOfOrder = names.findIndex((name, index) => name !== sorted[index]);
  if (outOfOrder >= 0) {
    die(
      `The ${label.toLowerCase()} manifest is not sorted, which makes its diffs unreadable.`,
      `first out of order: ${names[outOfOrder]} (expected ${sorted[outOfOrder]})`,
    );
  }

  return names;
}

function readSource(file) {
  const source = readFileSync(file, 'utf8');
  const root = source.match(/<svg\b([^>]*)>([\s\S]*)<\/svg>/);
  if (!root) die(`Unrecognised SVG shape: ${file}`);

  const viewBox = root[1].match(/viewBox="([^"]+)"/)?.[1];
  if (!viewBox) die(`Source SVG has no viewBox: ${file}`);

  return { viewBox, body: root[2].replace(/\s+/g, ' ').trim() };
}

const names = readManifest(MANIFEST, 'ICON_NAMES', /^bi-[a-z0-9]+(-[a-z0-9]+)*$/, 'Icon');
const brandNames = readManifest(
  BRAND_MANIFEST,
  'BRAND_ICON_NAMES',
  /^brand-[a-z0-9]+(-[a-z0-9]+)*$/,
  'Brand icon',
);

const symbols = [];
const missing = [];

for (const name of names) {
  const file = join(ICON_DIR, `${name.slice('bi-'.length)}.svg`);
  if (!existsSync(file)) {
    missing.push([name, file]);
    continue;
  }

  const { viewBox, body } = readSource(file);

  // `fill` moves onto the <symbol> because the source carried it on the <svg> root
  // that is being discarded. `currentColor` is what lets one sprite serve both
  // themes, and it only resolves that way because the sprite is injected into the
  // host document rather than referenced as an external file.
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

const missingBrand = [];

for (const name of brandNames) {
  const file = join(BRAND_DIR, `${name}.svg`);
  if (!existsSync(file)) {
    missingBrand.push([name, file]);
    continue;
  }

  const { viewBox, body } = readSource(file);

  // No `fill="currentColor"` here, and that omission is the whole point of the
  // second lane: a brand mark keeps the colours its owner specifies, so it must
  // inherit nothing from the caller's `color`. Each source keeps its own viewBox
  // too — a <symbol> establishes its own viewport, so a 28-unit mark still scales
  // to whatever font-size the caller sets.
  symbols.push(`<symbol id="${name}" viewBox="${viewBox}">${body}</symbol>`);
}

if (missingBrand.length > 0) {
  die(
    'These brand marks are in the manifest but have no source SVG:',
    ...missingBrand.map(([name, file]) => `  ${name}  (looked for ${file})`),
    '',
    `Add the file under ${BRAND_DIR}, or remove the entry.`,
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
  `icon sprite OK — ${names.length} glyphs + ${brandNames.length} brand marks, ` +
    `${(sprite.length / 1024).toFixed(1)} kB → public/icons/sprite.svg`,
);
