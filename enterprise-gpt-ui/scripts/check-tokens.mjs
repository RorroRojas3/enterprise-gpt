#!/usr/bin/env node
// Asserts that the design tokens in the app still match the design bundle, and that
// the colours duplicated into index.html's pre-paint shell still match the tokens.
// US-105.
//
// This is the acceptance criterion made executable: "the [data-bs-theme=light] and
// [data-bs-theme=dark] blocks carry the same custom properties with the same values".
import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const REPO_ROOT = resolve(UI_ROOT, '..');
const THEME_CSS = join(REPO_ROOT, 'docs', 'design', 'project', 'theme.css');
const TOKENS = join(UI_ROOT, 'src', 'styles', '_tokens.scss');
const INDEX = join(UI_ROOT, 'src', 'index.html');

function die(...lines) {
  console.error('\nToken check FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

if (!existsSync(THEME_CSS)) {
  die(
    `The design bundle is not present: ${THEME_CSS}`,
    'This check compares the app against it, so it cannot run.',
  );
}

/**
 * Normalises so `#FFFFFF` equals `#ffffff`, and `rgba(33,168,216,.45)` equals the
 * spaced, leading-zero form Prettier writes. Only formatting is ignored — never a
 * value.
 */
const normalize = (value) =>
  value
    .trim()
    .toLowerCase()
    .replaceAll(' ', '')
    .replace(/;$/, '')
    .replace(/(^|[(,])0\./g, '$1.');

/** Pulls `--name: value` pairs out of the block a selector opens. */
function declarationsIn(source, selectorPattern) {
  const start = source.search(selectorPattern);
  if (start < 0) return null;
  const open = source.indexOf('{', start);
  const close = source.indexOf('}', open);
  if (open < 0 || close < 0) return null;

  const pairs = new Map();
  for (const [, name, value] of source
    .slice(open + 1, close)
    .matchAll(/--([\w-]+)\s*:\s*([^;}]+)/g)) {
    pairs.set(name, normalize(value));
  }
  return pairs;
}

const themeCss = readFileSync(THEME_CSS, 'utf8');
const tokensScss = readFileSync(TOKENS, 'utf8');

// theme.css is one long line per block; _tokens.scss carries the values in two Sass
// maps, so the maps are what gets compared.
function mapEntries(source, name) {
  const start = source.indexOf(`$${name}: (`);
  if (start < 0) die(`Could not find the $${name} map.`, `file: ${TOKENS}`);
  const close = source.indexOf('\n);', start);
  const body = source.slice(start, close);

  const pairs = new Map();
  // The value runs to the comma that ends the entry, which is not the same as the
  // first comma — `rgba(33, 168, 216, .45)` contains three of its own.
  for (const [, key, value] of body.matchAll(/'([\w-]+)'\s*:\s*((?:[^,(\n]|\([^)]*\))+)/g)) {
    pairs.set(key, normalize(value));
  }
  return pairs;
}

const expected = {
  light: declarationsIn(themeCss, /:root,\s*\[data-bs-theme=light\]/),
  dark: declarationsIn(themeCss, /\[data-bs-theme=dark\]/),
};
const actual = { light: mapEntries(tokensScss, 'light'), dark: mapEntries(tokensScss, 'dark') };

const problems = [];
for (const theme of ['light', 'dark']) {
  if (!expected[theme]) die(`Could not find the ${theme} block in ${THEME_CSS}.`);

  for (const [name, value] of expected[theme]) {
    const mine = actual[theme].get(name);
    if (mine === undefined) problems.push(`${theme}: --${name} is missing from _tokens.scss`);
    else if (mine !== value) {
      problems.push(`${theme}: --${name} is ${mine} here but ${value} in theme.css`);
    }
  }
}

// The one value the acceptance criterion names outright.
if (actual.light.get('brand') !== '#14324f' || actual.dark.get('brand') !== '#21a8d8') {
  problems.push(
    `--brand must flip #14324F → #21A8D8; it is ` +
      `${actual.light.get('brand')} → ${actual.dark.get('brand')}`,
  );
}

// index.html duplicates two colours per theme because it paints before any
// stylesheet exists. That duplication is only safe while it stays in step.
const index = readFileSync(INDEX, 'utf8');
const shell = {
  light: declarationsInline(index, /:root\s*\{/),
  dark: declarationsInline(index, /:root\[data-bs-theme='dark'\]\s*\{/),
};

function declarationsInline(source, selectorPattern) {
  const start = source.search(selectorPattern);
  if (start < 0) return null;
  const open = source.indexOf('{', start);
  const close = source.indexOf('}', open);
  const block = source.slice(open + 1, close);
  return {
    background: normalize(block.match(/background:\s*([^;]+)/)?.[1] ?? ''),
    color: normalize(block.match(/[^-]color:\s*([^;]+)/)?.[1] ?? ''),
  };
}

for (const theme of ['light', 'dark']) {
  const painted = shell[theme];
  if (!painted) {
    problems.push(`index.html has no :root rule for the ${theme} pre-paint shell`);
    continue;
  }
  const bg = actual[theme].get('bs-body-bg');
  const fg = actual[theme].get('bs-body-color');
  if (painted.background !== bg) {
    problems.push(`index.html ${theme} background is ${painted.background}, tokens say ${bg}`);
  }
  if (painted.color !== fg) {
    problems.push(`index.html ${theme} color is ${painted.color}, tokens say ${fg}`);
  }
}

// The storage key is duplicated between the pre-paint script and the application, and
// a mismatch silently loses every user's stored preference exactly once. US-205 moved
// ownership of it into the localStorage allowlist, which is where this now reads it.
const preferences = readFileSync(
  join(UI_ROOT, 'src', 'app', 'core', 'storage', 'local-preferences.ts'),
  'utf8',
);
const serviceKey = preferences.match(/\btheme:\s*'([^']+)'/)?.[1];
const scriptKey = index.match(/localStorage\.getItem\('([^']+)'\)/)?.[1];
if (!serviceKey || serviceKey !== scriptKey) {
  problems.push(
    `The theme storage key differs: PREFERENCE_KEYS.theme is ${serviceKey}, ` +
      `the pre-paint script in index.html uses ${scriptKey}`,
  );
}

if (problems.length > 0) {
  die(...problems, '', `tokens:   ${TOKENS}`, `design:   ${THEME_CSS}`, `shell:    ${INDEX}`);
}

console.log(
  `tokens OK — ${expected.light.size} light and ${expected.dark.size} dark properties match ` +
    `theme.css, and the pre-paint shell matches the tokens`,
);
