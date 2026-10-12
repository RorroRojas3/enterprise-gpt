#!/usr/bin/env node
// Asserts that index.html's pre-paint colours match _tokens.scss (the source of record), that
// the code surface and focus ring clear WCAG contrast in both themes, and that the two copies
// of the breakpoint table agree.
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { stripComments, walk } from './lib/source-scan.mjs';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const TOKENS = join(UI_ROOT, 'src', 'styles', '_tokens.scss');
const INDEX = join(UI_ROOT, 'src', 'index.html');

function die(...lines) {
  console.error('\nToken check FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

/** Ignores formatting only (case, spaces, Prettier's leading zeros), never a value. */
const normalize = (value) =>
  value
    .trim()
    .toLowerCase()
    .replaceAll(' ', '')
    .replace(/;$/, '')
    .replace(/(^|[(,])0\./g, '$1.');

const tokensScss = readFileSync(TOKENS, 'utf8');

function mapEntries(source, name) {
  const start = source.indexOf(`$${name}: (`);
  if (start < 0) die(`Could not find the $${name} map.`, `file: ${TOKENS}`);
  const close = source.indexOf('\n);', start);
  const body = source.slice(start, close);

  const pairs = new Map();
  // An entry ends at its own comma, not the first: `rgba(33, 168, 216, .45)` holds three.
  for (const [, key, value] of body.matchAll(/'([\w-]+)'\s*:\s*((?:[^,(\n]|\([^)]*\))+)/g)) {
    pairs.set(key, normalize(value));
  }
  return pairs;
}

const actual = { light: mapEntries(tokensScss, 'light'), dark: mapEntries(tokensScss, 'dark') };

const problems = [];

if (actual.light.get('brand') !== '#14324f' || actual.dark.get('brand') !== '#21a8d8') {
  problems.push(
    `--brand must flip #14324F → #21A8D8; it is ` +
      `${actual.light.get('brand')} → ${actual.dark.get('brand')}`,
  );
}

// index.html duplicates two colours per theme because it paints before any stylesheet loads.
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

// The pre-paint script duplicates the theme storage key; a mismatch silently drops every
// user's stored preference.
const preferences = readFileSync(
  join(UI_ROOT, 'src', 'app', 'shared', 'utils', 'storage', 'local-preferences.ts'),
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

// The code surface is dark in both themes, so its legibility rests on this palette alone.
// `over` measures head-bar controls on the 8% white wash they sit on while hovered or
// confirming (_markdown.scss).
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
  // SC 1.4.11: the Copy control's focus ring and the streaming caret are --accent on code.
  { background: 'code-head', foregrounds: ['accent'], minimum: NON_TEXT_MINIMUM },
  // The Generated badge is small bold text on --surface, so it takes --brand: --accent
  // measures 2.74:1 there in light.
  { background: 'surface', foregrounds: ['brand'], minimum: TEXT_MINIMUM },
  { background: 'code-bg', foregrounds: ['accent'], minimum: NON_TEXT_MINIMUM },
  // Usage dashboard marks (SC 1.4.11) on the card and the bar track. The line takes --brand
  // because it is the daily series' only presentation; bars two to ten keep an unmeasured
  // --accent fill because each carries its value as text.
  {
    background: 'surface',
    foregrounds: ['brand', 'ok', 'muted', 'warn'],
    minimum: NON_TEXT_MINIMUM,
  },
  {
    background: 'surface-2',
    foregrounds: ['brand', 'ok', 'muted', 'warn'],
    minimum: NON_TEXT_MINIMUM,
  },
  // --focus-ring on every surface a ring is drawn on; FOCUS_SURFACE_EXCLUSIONS records the rest.
  // An offset ring sits behind its control, not on the fill, so an inset ring on a filled
  // control would escape this table: the two that exist (menu.scss, project-picker.scss) set
  // the measured --surface-2. --fail is the invalid field's ring (_kit.scss).
  { background: 'surface', foregrounds: ['fail'], minimum: NON_TEXT_MINIMUM },
  {
    background: 'surface',
    foregrounds: ['focus-ring'],
    minimum: NON_TEXT_MINIMUM,
  },
  {
    background: 'surface-2',
    foregrounds: ['focus-ring'],
    minimum: NON_TEXT_MINIMUM,
  },
  {
    background: 'bs-body-bg',
    foregrounds: ['focus-ring'],
    minimum: NON_TEXT_MINIMUM,
  },
  {
    background: 'active-bg',
    foregrounds: ['focus-ring'],
    minimum: NON_TEXT_MINIMUM,
  },
  {
    background: 'think-bg',
    foregrounds: ['focus-ring'],
    minimum: NON_TEXT_MINIMUM,
  },
  // --warn is running text as well as icons, so it takes the text minimum. `under` resolves
  // dark --warn-bg, an rgba, over the card it sits on.
  { background: 'surface', foregrounds: ['warn'], minimum: TEXT_MINIMUM },
  { background: 'bs-body-bg', foregrounds: ['warn'], minimum: TEXT_MINIMUM },
  { background: 'warn-bg', under: 'surface', foregrounds: ['warn'], minimum: TEXT_MINIMUM },
  // The active rail entry, sidebar link and paginator page draw --brand text on --active-bg.
  // The surfaces guard below checks only the ring against each background, never its text.
  { background: 'active-bg', foregrounds: ['brand'], minimum: TEXT_MINIMUM },
  // --brand marks on card and page (selected thumb, starred conversation, active filter). Text
  // minimum even for icons: the token is running text on these backgrounds too.
  { background: 'surface', foregrounds: ['brand'], minimum: TEXT_MINIMUM },
  { background: 'bs-body-bg', foregrounds: ['brand'], minimum: TEXT_MINIMUM },
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
const RGBA = /^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)$/;

/** A translucent token value as {@link composite} takes it, or null if it is not one. */
function asWash(value) {
  const parts = RGBA.exec(value ?? '');
  if (parts === null) return null;

  const hex = (channel) => Number(channel).toString(16).padStart(2, '0');
  return { colour: '#' + hex(parts[1]) + hex(parts[2]) + hex(parts[3]), alpha: Number(parts[4]) };
}
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
  for (const { background, foregrounds, minimum, over, under } of CONTRAST_PAIRS) {
    let backgroundValue = palette[theme].get(background);
    let backgroundName = `--${background}`;

    // A token translucent in one theme only (--warn-bg) is measured as what it resolves to
    // over `under`, rather than reported unmeasurable.
    const tint = asWash(backgroundValue);
    if (tint !== null && under !== undefined) {
      const beneath = palette[theme].get(under);
      if (!HEX.test(beneath ?? '')) {
        problems.push(unmeasurable(theme, under, beneath));
        continue;
      }
      backgroundValue = composite(beneath, tint);
      backgroundName = `--${background} over --${under}`;
    }

    if (!HEX.test(backgroundValue ?? '')) {
      problems.push(unmeasurable(theme, background, backgroundValue));
      continue;
    }

    const surface = over === undefined ? backgroundValue : composite(backgroundValue, over);
    const surfaceName = over === undefined ? backgroundName : `${backgroundName} + hover wash`;

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

// A new surface would leave its ring measured by nothing, so surfaces are read from the
// stylesheets, not token names (--brand and --accent sit under focusable content too). Each
// must be measured against the ring or excluded here.
const FOCUS_RING = 'focus-ring';

/** Why a surface the ring could land on is not measured against it. */
const FOCUS_SURFACE_EXCLUSIONS = {
  bubble: 'no focusable content on it; the message footer sits below the bubble',
  'btnP-bg':
    'the ring is offset, so it sits on the page behind the button, not the fill — and as the ' +
    "switch's on-state track it is inside a menu row whose ring is inset at the row's own " +
    'edge, nine pixels clear of the 28x16 track',
  'btnP-hover': 'the same button, hovered or active — and the ring is offset off it too',
  brand: 'the same — offset rings on the skip link, the sub-nav pill and the user footer',
  accent: 'the same, plus the code surface, where the ring *is* --accent and is measured above',
  'code-bg': 'the code surface draws its ring from --accent, measured above',
  'warn-bg': 'a translucent wash in dark; this check reads six-digit hex only',
  fail: 'measured above, as the invalid field ring rather than as a surface',
  'tooltip-bg':
    'the flyout is an aria-hidden span holding text and nothing else — no control ' +
    'is ever focused on it, and the host it names is on the page surface',
  'surface-2': 'measured above',
  'code-head': 'the code block head draws its ring from --accent, measured above',
  // Shapes, not surfaces: nothing is focused on them, and a control beside one sits on the
  // measured surface underneath.
  ok: 'a StatusDot fill — an 8px dot, not a surface',
  warn: 'a StatusDot fill, and a 10% wash on the two deactivate dialogs',
  muted:
    'a StatusDot fill, and the switch thumb and its off-state track border — the menu row ' +
    'holding the switch draws its ring inset at its own edge, never adjacent to the track',
  'provider-azure-openai': 'a provider dot in frame 2b',
  'provider-bedrock': 'a provider dot in frame 2b',
  'provider-anthropic': 'a provider dot in frame 2b',
  'toast-accent': "the toast's 4px left edge",
  'bs-border-color': 'a 1px separator rule in the menu and the picker',
  'btnP-fg': 'the switch thumb — a 10px circle inside a track, and the track is not focusable',
};

const focusSurfaces = new Set(
  CONTRAST_PAIRS.filter(({ foregrounds }) => foregrounds.includes(FOCUS_RING)).map(
    ({ background }) => background,
  ),
);

/**
 * Every token a stylesheet paints as a background. `--*-bg: var(--x)` counts too: Bootstrap
 * fills are set that way (`--bs-btn-hover-bg: var(--btnP-hover)`).
 */
const painted = new Set();
for (const file of walk(join(UI_ROOT, 'src'))) {
  if (!file.endsWith('.scss')) continue;
  const source = stripComments(readFileSync(file, 'utf8'));
  const declarations = source.matchAll(/(?:background(?:-color|-image)?|--[\w-]*bg)\s*:([^;}]*)/g);
  for (const [, value] of declarations) {
    for (const [, name] of value.matchAll(/var\(--([\w-]+)\)/g)) {
      painted.add(name);
    }
  }
}

for (const name of painted) {
  if (focusSurfaces.has(name) || Object.hasOwn(FOCUS_SURFACE_EXCLUSIONS, name)) continue;
  problems.push(
    `--${name} is painted as a background, so a focus ring can land on it, but ` +
      `CONTRAST_PAIRS does not measure --${FOCUS_RING} against it. Add the pair, or ` +
      `record why not in FOCUS_SURFACE_EXCLUSIONS.`,
  );
}

// A stale exclusion rots the same way: it reads as a decision about a live surface.
for (const name of Object.keys(FOCUS_SURFACE_EXCLUSIONS)) {
  if (!painted.has(name)) {
    problems.push(
      `FOCUS_SURFACE_EXCLUSIONS records --${name}, but nothing paints it as a ` +
        `background any more. Remove the entry.`,
    );
  }
}

// CONTRAST_PAIRS is hand-written, so a repointed `.token.keyword` would leave it measuring a
// token nothing renders.
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

// ── Breakpoint parity ─────────────────────────────────────────────────────────
// Sass cannot read a TS constant nor `matchMedia` a Sass variable, so both copies exist and are
// compared. Each maximum must sit 0.02px below a minimum: `767px` beside `768px` leaves a band
// matching no mode.
{
  // Comment-stripped: the prose in both files may quote the wrong queries.
  const scss = stripComments(
    readFileSync(join(UI_ROOT, 'src', 'styles', '_breakpoints.scss'), 'utf8'),
  );
  const ts = stripComments(
    readFileSync(
      join(UI_ROOT, 'src', 'app', 'shared', 'utils', 'layout', 'breakpoints.ts'),
      'utf8',
    ),
  );

  const read = (source, pattern, what) => {
    const found = source.match(pattern);
    if (!found) {
      problems.push(`Could not read ${what} — the breakpoint parity check cannot run`);
      return null;
    }
    return found[1];
  };

  const pairs = [
    [
      'tablet',
      read(scss, /\$bp-tablet-min:\s*(\d+)px/, '$bp-tablet-min'),
      read(ts, /TABLET_MIN_WIDTH = (\d+)/, 'TABLET_MIN_WIDTH'),
    ],
    [
      'desktop',
      read(scss, /\$bp-desktop-min:\s*(\d+)px/, '$bp-desktop-min'),
      read(ts, /DESKTOP_MIN_WIDTH = (\d+)/, 'DESKTOP_MIN_WIDTH'),
    ],
  ];

  for (const [name, fromScss, fromTs] of pairs) {
    if (fromScss !== null && fromTs !== null && fromScss !== fromTs) {
      problems.push(
        `The ${name} breakpoint is ${fromScss}px in _breakpoints.scss and ${fromTs}px in ` +
          'breakpoints.ts. They describe one edge and must be the same number.',
      );
    }
  }

  const minima = new Set(pairs.flatMap(([, value]) => (value === null ? [] : [Number(value)])));
  for (const [file, source] of [
    ['_breakpoints.scss', scss],
    ['breakpoints.ts', ts],
  ]) {
    for (const [, maximum] of source.matchAll(/max-width:\s*([\d.]+)px/g)) {
      // Rounded: `767.98 + 0.02` lands on exactly 768 in doubles only by luck.
      if (!minima.has(Math.round((Number(maximum) + 0.02) * 100) / 100)) {
        problems.push(
          `${file} carries a maximum of ${maximum}px, which is not 0.02px below any ` +
            'declared minimum. A whole-number maximum beside a whole-number minimum ' +
            'leaves a fractional viewport matching neither mode.',
        );
      }
    }
  }
}

if (problems.length > 0) {
  die(...problems, '', `tokens:   ${TOKENS}`, `shell:    ${INDEX}`);
}

const measured =
  CONTRAST_PAIRS.reduce((count, { foregrounds }) => count + foregrounds.length, 0) * 2;

console.log(
  `tokens OK — ${actual.light.size} light and ${actual.dark.size} dark properties parsed, ` +
    `the pre-paint shell matches the tokens, the two breakpoints agree, and ${measured} ` +
    `code-surface and focus-ring pairs clear their WCAG minimum`,
);
