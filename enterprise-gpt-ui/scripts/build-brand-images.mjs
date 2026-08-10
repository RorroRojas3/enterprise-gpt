#!/usr/bin/env node
// Derives the app's brand assets from the design bundle's PNGs. US-109.
//
// Run on demand, not from prebuild, and commit the outputs. The sources are shaded
// raster illustrations with gradients — there is no SVG to vendor and hand-authoring
// one would not be the brand — but the originals are 140–257 kB each for images that
// render at 250 CSS px at most. Keeping sharp's native binary and the ../docs path
// off the build's critical path is worth ~55 kB of binaries in git that change
// approximately never.
import { existsSync, mkdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const SOURCE_DIR = resolve(UI_ROOT, '..', 'docs', 'design', 'project', 'assets');
const OUT_DIR = join(UI_ROOT, 'public', 'brand');

/**
 * One size per variant, at 2× the largest place it renders: the wordmark peaks at
 * 250 px on the sign-in interstitial, the mark at 58 px on the bootstrap-failure
 * screen. A srcset ladder would trade a few decoded pixels for a second request,
 * which is the wrong way round for an image this small.
 */
const IMAGES = [
  { source: 'logo-full.png', out: 'logo-full.webp', width: 512 },
  { source: 'logo-full-dark.png', out: 'logo-full-dark.webp', width: 512 },
  { source: 'logo-mark.png', out: 'logo-mark.webp', width: 128 },
  { source: 'logo-mark-dark.png', out: 'logo-mark-dark.webp', width: 128 },
];

if (!existsSync(SOURCE_DIR)) {
  console.error(`\nBrand image build FAILED\n\n  Design bundle not found: ${SOURCE_DIR}\n`);
  process.exit(1);
}

mkdirSync(OUT_DIR, { recursive: true });

const missing = IMAGES.filter(({ source }) => !existsSync(join(SOURCE_DIR, source)));
if (missing.length > 0) {
  console.error('\nBrand image build FAILED — missing sources:\n');
  for (const { source } of missing) console.error('  ' + join(SOURCE_DIR, source));
  console.error('');
  process.exit(1);
}

let before = 0;
let after = 0;

for (const { source, out, width } of IMAGES) {
  const from = join(SOURCE_DIR, source);
  const to = join(OUT_DIR, out);
  const meta = await sharp(from).metadata();

  await sharp(from)
    .resize({ width, withoutEnlargement: true })
    .webp({ quality: 82, effort: 6 })
    .toFile(to);

  before += statSync(from).size;
  after += statSync(to).size;
  console.log(
    `  ${source} ${meta.width}×${meta.height} → ${out} ${width}px ` +
      `(${(statSync(to).size / 1024).toFixed(1)} kB)`,
  );
}

console.log(
  `brand images OK — ${(before / 1024).toFixed(0)} kB of PNG → ${(after / 1024).toFixed(1)} kB of WebP`,
);
