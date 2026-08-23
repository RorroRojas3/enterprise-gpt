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
import { stripComments, walk } from './lib/source-scan.mjs';

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
  // US-1302. The usage dashboard's marks, measured as graphical objects (SC 1.4.11): the area
  // chart's line and the donut's three arcs on the card surface, and the leading bar's fill on
  // the bar track. The line takes --brand rather than the --accent frame `5j` draws precisely
  // because of this gate — --accent is 2.74:1 on --surface in the light theme, and that line is
  // the sole visual presentation of the daily series, so it has to clear the threshold.
  //
  // One mark is deliberately not measured: the --accent fill on bars two through ten. Every bar
  // carries its own name and value as text beside it, so those fills are redundant presentation
  // rather than something the reader depends on — which is just as well, because --accent could
  // not clear 3:1 on --surface-2 either, and the board tints them with it.
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
  // US-1401. `_tokens.scss` claims --focus-ring clears 3:1 "on every surface in
  // the kit"; until now nothing measured it, which is how four controls came to
  // draw their ring from --ring instead and fail SC 1.4.11 with every gate green.
  //
  // These five are the surfaces a ring is drawn *on*: the page, both card surfaces,
  // the hovered/active row and the thinking panel. Everything else a stylesheet uses
  // as a background is excluded on the record below, and the guard after
  // CONTRAST_PAIRS is what makes that a decision rather than an oversight.
  //
  // The recurring reason for an exclusion is worth stating once: **a ring drawn with
  // `outline-offset` is not on the control it belongs to.** It sits in the gap, on
  // whatever is behind — so a filled control's own colour is not the adjacent one.
  // That is why --btnP-bg is absent even though the ring measures **1.00:1** against
  // it in dark, where the button fill *is* the focus token. The corollary is the
  // trap: **an inset ring on a filled control would be invisible and this table would
  // not catch it.** The two inset rings that exist (`menu.scss`,
  // `project-picker.scss`) set `--surface-2` on the same `:focus-visible` rule, which
  // is measured.
  //
  // --fail is measured too: `_kit.scss` draws the invalid field's ring from it, so it
  // is a focus indicator whether or not it is named like one.
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
  // US-1405, closing the `--warn` open question in the PRD's §9. Light `#A96A10`
  // measured 4.41:1 on white, 4.21:1 on the page and 3.99:1 on its own tinted panel —
  // all below AA for text under 18.66px — and it is used as a `color` in eight
  // stylesheets. The value moved upstream in `docs/design/project/theme.css`, because
  // this check enforces parity with the design bundle and a local override would fail
  // it; these three pairs are what stop it drifting back.
  //
  // TEXT_MINIMUM rather than NON_TEXT: some of the eight are icons, which would only
  // need 3:1, but several are running text and the same token serves both. Measuring
  // the stricter of the two is the honest reading.
  //
  // `under` is what makes the third measurable in dark, where `--warn-bg` is an rgba
  // over the card it sits on rather than an opaque colour.
  { background: 'surface', foregrounds: ['warn'], minimum: TEXT_MINIMUM },
  { background: 'bs-body-bg', foregrounds: ['warn'], minimum: TEXT_MINIMUM },
  { background: 'warn-bg', under: 'surface', foregrounds: ['warn'], minimum: TEXT_MINIMUM },
  // US-1405, and the pair the axe run found rather than this table: the active entry in
  // the administration rail, the sidebar's active navigation link and the paginator's
  // current page all render `--brand` on `--active-bg`, which measured **4.29:1** in dark
  // against a 4.5:1 minimum for 13.5px text. The board specifies that pairing, so the
  // correction went upstream in `theme.css` — dark `--active-bg` `#173A58` → `#16354D` —
  // under the PRD's rule that this document wins over the boards on accessibility.
  //
  // It went undetected for three stories because nothing measured it: the surfaces guard
  // below checks every painted background against `--focus-ring`, not against the text
  // drawn on it. This row is what makes the fix permanent.
  { background: 'active-bg', foregrounds: ['brand'], minimum: TEXT_MINIMUM },
  // US-1103's selected thumb, and the several `color: var(--brand)` marks beside it — the
  // starred conversation, the favourited project, the active document filter. All of them
  // draw on a card or on the page, and none of them was measured until this row: the pair
  // above went undetected for three stories for exactly that reason, and adding a third
  // `--brand` site without adding its background here would repeat the omission.
  //
  // TEXT_MINIMUM even though the thumb is an icon, which SC 1.4.11 would meet at 3:1. The
  // token serves running text at these two backgrounds as well, and both pairs clear the
  // stricter figure with room to spare, so measuring the weaker one buys nothing but a
  // guard that would not notice the value moving.
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

    // A token that is translucent in one theme and opaque in the other — `--warn-bg` is
    // a hex in light and an rgba in dark — is still one surface, and the reader sees
    // whatever it resolves to. `under` names what it is painted on so both themes can be
    // measured, rather than the dark half being reported as unmeasurable.
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

// US-1401. The focus pairs above are hand-written too, and the way that list rots is
// a *new* surface: a ring drawn on it would be measured by nothing, and every gate
// would stay green.
//
// So the surfaces are read out of the stylesheets rather than guessed from token
// names. A name heuristic looked tidier and was wrong — `--brand` and `--accent` are
// both painted as backgrounds under focusable content (`.skip-link`, the recording
// pill, the active sub-nav pill, the user footer, the attachment chip) and neither
// name ends in `bg`. Every token any stylesheet sets as a background has to be either
// measured against the ring or excluded on the record here.
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
  // Six fills that are shapes rather than surfaces: nothing is ever focused *on*
  // one, so no ring is ever adjacent to one. A control focused beside them is on
  // the surface underneath, which is measured.
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
 * Every token a stylesheet paints as a background, which is every surface there is.
 *
 * Two forms, because the app fills a control both ways. The direct one is
 * `background: var(--x)`. The indirect one is `--anything-bg: var(--x)`, which is how
 * **every** Bootstrap component fill is set — `_bootstrap-overrides.scss` assigns
 * `--bs-btn-hover-bg: var(--btnP-hover)` and Bootstrap paints from there. Matching
 * only the direct form missed the hovered primary button entirely, and missed it
 * *silently*, which is the failure this whole guard replaced a name heuristic to
 * avoid.
 *
 * The value is captured before the tokens are read out of it, so a declaration
 * naming two — a gradient — records both rather than the last.
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

// And the record has to stay honest in the other direction too. An exclusion for a
// token nothing paints any more is the same rot as a missing one — it reads as a
// considered decision about a live surface, and the next reader trusts it.
for (const name of Object.keys(FOCUS_SURFACE_EXCLUSIONS)) {
  if (!painted.has(name)) {
    problems.push(
      `FOCUS_SURFACE_EXCLUSIONS records --${name}, but nothing paints it as a ` +
        `background any more. Remove the entry.`,
    );
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

// ── Keyframe parity (US-1404) ────────────────────────────────────────────────
// US-1404's fourth criterion asks that all four keyframes "run as theme.css defines
// them". `check-forbidden-apis.mjs` refuses a fifth; this holds the four to the design
// bundle, which nothing did before — a transcription that drifted would have changed
// how the app moves with every gate still green.
//
// Compared with whitespace and the optional trailing semicolon removed, and nothing
// else: `_motion.scss` is Prettier-formatted and theme.css is minified, so those are
// the only differences that are *not* a change of behaviour.
{
  const MOTION = join(UI_ROOT, 'src', 'styles', '_motion.scss');
  const motion = stripComments(readFileSync(MOTION, 'utf8'));
  const design = readFileSync(THEME_CSS, 'utf8');

  /**
   * The body of one `@keyframes`, brace-matched so a nested block cannot end it early.
   *
   * Located with `indexOf` rather than a built RegExp: the next character after the name
   * is checked directly, which is both simpler and what stops `spin` matching `spinner`.
   */
  const bodyOf = (source, name) => {
    const marker = '@keyframes ' + name;
    let at = -1;
    for (let from = source.indexOf(marker); from >= 0; from = source.indexOf(marker, from + 1)) {
      const next = source[from + marker.length];
      if (next === '{' || next === ' ' || next === '\n' || next === '\r' || next === '\t') {
        at = from;
        break;
      }
    }
    if (at < 0) return null;

    let depth = 0;
    for (let i = source.indexOf('{', at); i < source.length; i++) {
      if (source[i] === '{') depth++;
      else if (source[i] === '}' && --depth === 0) {
        return source
          .slice(source.indexOf('{', at) + 1, i)
          .replace(/\s+/g, '')
          .replace(/;(?=\}|$)/g, '')
          .toLowerCase();
      }
    }
    return null;
  };

  for (const name of ['blink', 'spin', 'ringpulse', 'ridgedash']) {
    const ours = bodyOf(motion, name);
    const theirs = bodyOf(design, name);

    if (ours === null) {
      problems.push(`_motion.scss no longer defines @keyframes ${name}, which theme.css does`);
    } else if (theirs === null) {
      problems.push(`theme.css no longer defines @keyframes ${name} — the design bundle moved`);
    } else if (ours !== theirs) {
      problems.push(
        `@keyframes ${name} has drifted from theme.css. Design: ${theirs}. App: ${ours}`,
      );
    }
  }
}

// ── Breakpoint parity (US-1403) ───────────────────────────────────────────────
// The three application modes are stated twice by necessity — Sass cannot read a
// TypeScript constant and `matchMedia` cannot read a Sass variable — so the two copies
// are compared here rather than trusted. `check-forbidden-apis.mjs` stops a third copy
// appearing; this stops the two that must exist from drifting.
//
// Both halves are checked: the whole-number minimum, and the fractional maximum in each
// mixin. The fraction is the half that rots silently — a `767px` maximum beside a
// `768px` minimum looks tidier and leaves a 0.98px band matching no mode at all.
{
  // Comment-stripped, because both files *document* the fractional-maximum bug by
  // quoting the wrong queries. Scanning the prose would fail the gate on its own
  // explanation.
  const scss = stripComments(
    readFileSync(join(UI_ROOT, 'src', 'styles', '_breakpoints.scss'), 'utf8'),
  );
  const ts = stripComments(
    readFileSync(join(UI_ROOT, 'src', 'app', 'shared', 'layout', 'breakpoints.ts'), 'utf8'),
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

  // Every maximum, in both files, is one hundredth below a minimum that exists.
  const minima = new Set(pairs.flatMap(([, value]) => (value === null ? [] : [Number(value)])));
  for (const [file, source] of [
    ['_breakpoints.scss', scss],
    ['breakpoints.ts', ts],
  ]) {
    for (const [, maximum] of source.matchAll(/max-width:\s*([\d.]+)px/g)) {
      // Rounded rather than compared raw: `767.98 + 0.02` lands on exactly 768 at double
      // precision by luck, and the next pair of numbers may not be as lucky.
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
  die(...problems, '', `tokens:   ${TOKENS}`, `design:   ${THEME_CSS}`, `shell:    ${INDEX}`);
}

const measured =
  CONTRAST_PAIRS.reduce((count, { foregrounds }) => count + foregrounds.length, 0) * 2;

console.log(
  `tokens OK — ${expected.light.size} light and ${expected.dark.size} dark properties match ` +
    `theme.css, the pre-paint shell matches the tokens, the four keyframes and the two ` +
    `breakpoints match it too, and ${measured} code-surface and focus-ring pairs clear ` +
    `their WCAG minimum`,
);
