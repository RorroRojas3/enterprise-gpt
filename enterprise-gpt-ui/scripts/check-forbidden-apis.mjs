#!/usr/bin/env node
// Text backstop for the APIs and origins that must not appear anywhere under src/.
// US-108.
//
// ESLint's no-restricted-syntax carries the bypassSecurityTrustHtml rule
// structurally, but it cannot see two things: a property name assembled at run time,
// and an inline `// eslint-disable-next-line`. A text scan sees both. Running it as
// a Node script rather than a CI grep step means Windows developers get the
// identical gate locally.
import { readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { stripComments, walk } from './lib/source-scan.mjs';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const SRC = join(UI_ROOT, 'src');

/**
 * Opt out of one line, for a case the scan is right about and the code is right
 * anyway — an inset ring, or a target focused only programmatically. Same shape as
 * `check-icon-names.mjs`'s, and deliberately per-line rather than per-file: an
 * exemption that names the reason at the site it applies to is reviewable, and one
 * listed in this file is not.
 *
 * **It applies only to rules marked `escapable`**, which is the two focus rules and
 * nothing else. A hatch that also waved through `bypassSecurityTrustHtml` would
 * defeat the backstop this whole script exists to be — the header above says it in
 * full: ESLint's rule can be turned off with an inline comment, and a text scan
 * seeing that is the entire point.
 */
const ESCAPE = 'focus-check-ignore-next-line';

const FORBIDDEN = [
  {
    pattern: /\bbypassSecurityTrustHtml\b/,
    label: 'bypassSecurityTrustHtml',
    why:
      'ngx-markdown performs the trust internally, after DOMPurify. A second call ' +
      'here is an unsanitised path to innerHTML, and model output is ' +
      'attacker-influenceable through uploaded documents and MCP tool results.',
    // Byte-for-byte upstream (US-107). It holds no DOM code, but exempting it means
    // this script can never be a reason to edit it.
    exempt: ['app/domain/stream/andes/assistant-ui.contract.ts'],
  },
  {
    // The URL form, not the bare hostname: naming an origin in prose is fine, and
    // comments are blanked out before scanning anyway.
    pattern: /(https?:)?\/\/(fonts\.googleapis\.com|fonts\.gstatic\.com)/,
    label: 'a Google Fonts origin',
    why: 'FR-51: the app issues zero third-party requests at run time. Fonts are self-hosted.',
    exempt: [],
  },
  {
    pattern: /(https?:)?\/\/(cdn\.jsdelivr\.net|unpkg\.com)/,
    label: 'a CDN origin',
    why: 'FR-51: Bootstrap, its icons, and every library come from npm, never a CDN.',
    exempt: [],
  },
  {
    // Unanchored on purpose. The specifier is spelled at least five ways in the
    // wild — bare, `~`-prefixed, `node_modules/…`-prefixed, relative through
    // `url()`, and as an `angular.json` styles entry — and only the path segment
    // is common to all of them. Prose mentions are safe: comments are blanked
    // before scanning, and this file is not itself scanned.
    pattern: /prismjs\/themes\//,
    label: "one of Prism's shipped stylesheets",
    why:
      'US-604: --code-bg is dark in both themes, so there is exactly one code ' +
      'surface and _prism.scss tints it from tokens. A second stylesheet would have ' +
      'to be swapped on a theme change, and a swap is what paints the light-styled ' +
      'first frame the story forbids. Neither of Prism’s themes matches the design ' +
      'palette either — change _prism.scss, not this.',
    exempt: [],
  },
  {
    // Writes only. Reading localStorage cannot leave a user's data on a shared
    // machine, and index.html's pre-paint script legitimately reads the theme before
    // any module exists.
    pattern: /\blocalStorage\s*\.\s*setItem\b/,
    label: 'a direct localStorage write',
    why:
      'US-205: after sign-out localStorage must hold the theme and sidebar ' +
      'preferences and nothing else. Route writes through core/storage/' +
      'local-preferences.ts so the surviving keys are enumerated in one reviewable ' +
      'place rather than spread across call sites.',
    exempt: [
      'app/core/storage/local-preferences.ts',
      // Asserts the wrapper itself, so it has to reach past it.
      'app/core/storage/local-preferences.spec.ts',
      // Plants a disallowed key on purpose, to prove sign-out does *not* sweep it —
      // which is what makes this check, rather than a runtime cleanup, the enforcement
      // point for US-205's second criterion.
      'app/shared/nav/user-footer/user-footer.spec.ts',
    ],
  },
  {
    // Suppressing the indicator is the failure US-1401 found four times: three
    // composer controls and the two form-field rules replaced it with a translucent
    // wash measuring about 1.2–1.4:1, and Bootstrap's own `.form-control:focus`
    // sets `outline: 0` besides. A line that removes it and puts nothing back is a
    // control a keyboard user cannot see themselves on.
    //
    // Not "…unless the same block sets a box-shadow": that would pass for a shadow
    // in any colour, which is exactly what was wrong before. The legitimate sites
    // carry the escape hatch and say why at the line.
    //
    // Every spelling that removes the ring, not only the canonical one: `outline: 0px`
    // and `outline : none` slip past a tighter pattern, and the two longhands do the
    // same job under another name.
    pattern: /(^|[;{\s])outline(-width|-style)?\s*:\s*(none|0[a-z%]*)\s*(!important)?\s*[;}]/,
    label: 'a suppressed focus indicator',
    escapable: true,
    why:
      'US-1401 / WCAG 2.1 SC 1.4.11: every interactive element needs a visible ' +
      'focus indicator at 3:1. `_focus.scss` draws one from --focus-ring for ' +
      'anything with no rule of its own; a rule that removes it must draw its own ' +
      `— an inset box-shadow for a full-bleed row — and say so with a \`${ESCAPE}\` comment.`,
    exempt: [],
  },
  {
    // --ring is the `ringpulse` keyframe's colour: 45% alpha, made to fade out from
    // under a control. Used as a focus indicator it composites to about 1.4:1 on
    // --surface, which is how four controls came to fail SC 1.4.11 while every gate
    // stayed green. --focus-ring is the token that was measured for the job.
    pattern: /var\(--ring\)/,
    label: 'the pulse colour used outside its keyframe',
    escapable: true,
    why:
      'US-1401: --ring exists for `ringpulse` and is translucent by design. The ' +
      'focus indicator is --focus-ring, which check-tokens.mjs measures against ' +
      'every surface it can land on; --ring is measured against nothing.',
    exempt: ['styles/_motion.scss'],
  },
  {
    // The three application modes are one decision, and it was spread across twelve
    // stylesheets and two components in six spellings — which is how `767px` and
    // `767.98px` came to sit in the same codebase meaning the same edge. They do not:
    // paired with a whole-number `min-width: 768px`, the whole-number maximum leaves a
    // viewport reported as 767.5px matching neither mode, and this application ships on
    // Windows, where display scaling produces exactly that.
    //
    // Component-local reflow points are untouched — 480px in the two admin form
    // dialogs, 575px in the project files row, 575.98px in the auth interstitial. This
    // rule names only the two numbers that mean "the application changed mode".
    pattern: /\((?:max|min)-width\s*:\s*(?:767(?:\.98)?|768|1023(?:\.98)?|1024)px/,
    label: 'a hardcoded application breakpoint',
    why:
      'US-1403: the three viewport modes are stated once, in styles/_breakpoints.scss ' +
      'and app/shared/layout/breakpoints.ts, and check-tokens.mjs keeps the two in ' +
      'step. Use the `bp.mobile` / `bp.tablet` / `bp.desktop` mixins in a stylesheet, ' +
      'or MOBILE_VIEWPORT / TABLET_VIEWPORT in TypeScript.',
    exempt: ['styles/_breakpoints.scss', 'app/shared/layout/breakpoints.ts'],
  },
  {
    // The application defines exactly four keyframes.
    //
    // It is why Bootstrap's `_spinners.scss` and `_placeholders.scss` are excluded from
    // the stylesheet — each would add a fifth — and why `_motion.scss` is not exempt
    // here: a fifth keyframe is no more welcome in that file than anywhere else.
    //
    // A word boundary after each name, or `@keyframes spinner` would pass as `spin`.
    pattern: /@keyframes\s+(?!(?:blink|spin|ringpulse|ridgedash)\b)/,
    label: 'a fifth keyframe',
    why:
      'The app defines exactly four keyframes — blink, spin, ringpulse and ' +
      'ridgedash. Anything else that has to move uses a transition on the ' +
      '--t-fast / --t-slow scale, which the reduced-motion block already suppresses.',
    exempt: [],
  },
];

const findings = [];

for (const file of walk(SRC)) {
  const rel = relative(SRC, file).replaceAll('\\', '/');
  const source = readFileSync(file, 'utf8');
  // .scss and .ts share C-style comments; .html has its own form.
  const scannable = stripComments(source, { html: rel.endsWith('.html') });
  const lines = scannable.split('\n');
  // Comments are blanked in `scannable`, so the escape hatch has to be read off
  // the original text.
  const raw = source.split('\n');

  for (const { pattern, label, why, exempt, escapable = false } of FORBIDDEN) {
    if (exempt.includes(rel)) continue;
    lines.forEach((line, index) => {
      if (escapable && (raw[index - 1] ?? '').includes(ESCAPE)) return;
      if (pattern.test(line)) {
        findings.push({ label, why, where: `src/${rel}:${index + 1}`, line: line.trim() });
      }
    });
  }
}

// `angular.json`'s `styles` array is the route ngx-markdown's own README
// prescribes for a Prism theme, and it is outside `src/` — so the scan above
// cannot see it, and neither can `check-initial-chunk.mjs`, which walks the JS
// graph and never meets a global stylesheet entry. The most likely way US-604's
// decision regresses is the one path no other gate covers.
const ANGULAR_JSON = join(UI_ROOT, 'angular.json');
const workspace = readFileSync(ANGULAR_JSON, 'utf8');
for (const [index, line] of workspace.split('\n').entries()) {
  if (/prismjs\/themes\//.test(line)) {
    const rule = FORBIDDEN.find(({ label }) => label.includes('Prism'));
    findings.push({
      label: rule.label,
      why: rule.why,
      where: `angular.json:${index + 1}`,
      line: line.trim(),
    });
  }
}

if (findings.length > 0) {
  console.error('\nForbidden API check FAILED\n');
  for (const { label, why, where, line } of findings) {
    console.error(`  ${label}`);
    console.error(`    ${where}`);
    console.error(`    ${line.length > 120 ? line.slice(0, 117) + '…' : line}`);
    console.error(`    ${why}\n`);
  }
  process.exit(1);
}

console.log(
  `forbidden APIs OK — none of ${FORBIDDEN.length} patterns appears under src/ or in angular.json`,
);
