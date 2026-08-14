#!/usr/bin/env node
// Asserts that the design tokens in the app still match the design bundle, that the
// colours duplicated into index.html's pre-paint shell still match the tokens, and
// that the code surface stays legible in both themes. US-105 / US-604.
//
// This is the acceptance criterion made executable: "the [data-bs-theme=light] and
// [data-bs-theme=dark] blocks carry the same custom properties with the same values".
//
// The contrast section at the end is US-604's. That story has no light Prism variant
// to swap to — the board fixes --code-bg dark in both themes — so what makes its
// "legible in dark mode" criterion true is the palette itself, and a criterion that
// is only true by inspection is a criterion that drifts. Measuring it here is what
// tells a future palette edit which colour it broke, and by how much.
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

// US-604. The code surface is dark in *both* themes by design, so nothing about it
// flips and no light variant exists to swap to — which means the whole of the story's
// legibility criterion rests on this palette. Both surfaces are measured because
// --code-bg and --code-head differ between the two maps even though the text on them
// does not.
//
// `over` is the state the element is actually in when it matters: the head bar's
// controls sit on a white 8% wash while hovered or confirming (_markdown.scss), and
// measuring the unhovered case alone would report a margin the user never has.
const TEXT_MINIMUM = 4.5;
const NON_TEXT_MINIMUM = 3;
const HOVER_WASH = { colour: '#ffffff', alpha: 0.08 };

const CONTRAST_PAIRS = [
  { background: 'code-bg', foregrounds: ['code-fg'], minimum: TEXT_MINIMUM },
  {
    background: 'code-bg',
    foregrounds: [
      'code-comment',
      'code-punct',
      'code-keyword',
      'code-string',
      'code-number',
      'code-function',
    ],
    minimum: TEXT_MINIMUM,
  },
  {
    background: 'code-head',
    foregrounds: ['code-head-fg', 'code-head-muted'],
    minimum: TEXT_MINIMUM,
  },
  {
    background: 'code-head',
    over: HOVER_WASH,
    foregrounds: ['code-head-fg', 'code-head-muted'],
    minimum: TEXT_MINIMUM,
  },
  // SC 1.4.11: the focus ring on the Copy control and the streaming caret are both
  // --accent on a code surface, and neither is text. The page's --focus-ring is the
  // wrong token here, which _markdown.scss says in prose — this is that claim
  // measured.
  { background: 'code-head', foregrounds: ['accent'], minimum: NON_TEXT_MINIMUM },
  { background: 'code-bg', foregrounds: ['accent'], minimum: NON_TEXT_MINIMUM },
];

/** WCAG 2.1 relative luminance of a `#rrggbb` value. */
function luminance(hex) {
  const channels = [1, 3, 5].map((offset) => {
    const value = parseInt(hex.slice(offset, offset + 2), 16) / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(foreground, background) {
  const [lighter, darker] = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
  return (lighter + 0.05) / (darker + 0.05);
}

/** The opaque colour a translucent `wash` resolves to over `base`. */
function composite(base, wash) {
  const channels = [1, 3, 5].map((offset) => {
    const under = parseInt(base.slice(offset, offset + 2), 16);
    const over = parseInt(wash.colour.slice(offset, offset + 2), 16);
    return Math.round(over * wash.alpha + under * (1 - wash.alpha));
  });
  return '#' + channels.map((value) => value.toString(16).padStart(2, '0')).join('');
}

const HEX = /^#[0-9a-f]{6}$/;
const palette = {
  light: new Map([...actual.light, ...mapEntries(tokensScss, 'extra-light')]),
  dark: new Map([...actual.dark, ...mapEntries(tokensScss, 'extra-dark')]),
};

/** Reports a value this check cannot measure, which is a gap in the gate, not a token defect. */
function unmeasurable(theme, name, value) {
  return (
    `${theme}: --${name} is ${value ?? 'missing'}, which this check cannot measure — ` +
    `it reads six-digit hex only. Widen the parser in check-tokens.mjs.`
  );
}

for (const theme of ['light', 'dark']) {
  for (const { background, foregrounds, minimum, over } of CONTRAST_PAIRS) {
    const backgroundValue = palette[theme].get(background);
    if (!HEX.test(backgroundValue ?? '')) {
      problems.push(unmeasurable(theme, background, backgroundValue));
      continue;
    }

    const surface = over === undefined ? backgroundValue : composite(backgroundValue, over);
    const surfaceName = over === undefined ? `--${background}` : `--${background} + hover wash`;

    for (const foreground of foregrounds) {
      const foregroundValue = palette[theme].get(foreground);
      if (!HEX.test(foregroundValue ?? '')) {
        problems.push(unmeasurable(theme, foreground, foregroundValue));
        continue;
      }

      const ratio = contrast(foregroundValue, surface);
      if (ratio < minimum) {
        problems.push(
          `${theme}: --${foreground} ${foregroundValue} on ${surfaceName} ${surface} ` +
            `measures ${ratio.toFixed(2)}:1, below the ${minimum}:1 WCAG 2.1 AA minimum`,
        );
      }
    }
  }
}

// The list above is hand-written, so a stylesheet that repointed `.token.keyword` at
// a different token would leave the gate measuring one nothing renders. Every
// `--code-*` the two code stylesheets consume has to appear in it.
const gated = new Set(
  CONTRAST_PAIRS.flatMap(({ background, foregrounds }) => [background, ...foregrounds]),
);
for (const file of ['_prism.scss', '_markdown.scss']) {
  const source = readFileSync(join(UI_ROOT, 'src', 'styles', file), 'utf8');
  for (const [, name] of source.matchAll(/var\(--(code-[\w-]+)\)/g)) {
    if (!gated.has(name)) {
      problems.push(
        `${file} renders --${name} on the code surface, but CONTRAST_PAIRS does not measure it`,
      );
    }
  }
}

if (problems.length > 0) {
  die(...problems, '', `tokens:   ${TOKENS}`, `design:   ${THEME_CSS}`, `shell:    ${INDEX}`);
}

const measured =
  CONTRAST_PAIRS.reduce((count, { foregrounds }) => count + foregrounds.length, 0) * 2;

console.log(
  `tokens OK — ${expected.light.size} light and ${expected.dark.size} dark properties match ` +
    `theme.css, the pre-paint shell matches the tokens, and ${measured} code-surface pairs ` +
    `clear their WCAG minimum`,
);
