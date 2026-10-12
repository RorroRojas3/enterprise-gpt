#!/usr/bin/env node
// Text backstop for APIs and origins forbidden under src/. ESLint's rule misses a property
// name assembled at run time and an inline `// eslint-disable-next-line`; a text scan sees both.
import { readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { stripComments, walk } from './lib/source-scan.mjs';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const SRC = join(UI_ROOT, 'src');

/**
 * Per-line opt-out where the scan is right but the code is too (an inset ring, a target focused
 * only programmatically). Honoured only by `escapable` rules, so never for trust-HTML calls.
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
    // Byte-for-byte upstream: exempt so this script is never a reason to edit it.
    exempt: ['app/shared/models/andes/assistant-ui.contract.ts'],
  },
  {
    // The URL form, not the bare hostname: naming an origin in prose is fine.
    pattern: /(https?:)?\/\/(fonts\.googleapis\.com|fonts\.gstatic\.com)/,
    label: 'a Google Fonts origin',
    why: 'The app issues zero third-party requests at run time. Fonts are self-hosted.',
    exempt: [],
  },
  {
    pattern: /(https?:)?\/\/(cdn\.jsdelivr\.net|unpkg\.com)/,
    label: 'a CDN origin',
    why: 'Bootstrap, its icons, and every library come from npm, never a CDN.',
    exempt: [],
  },
  {
    // Unanchored: the specifier appears bare, `~`-prefixed, via node_modules, through `url()`
    // and in angular.json, and only the path segment is common to all of them.
    pattern: /prismjs\/themes\//,
    label: "one of Prism's shipped stylesheets",
    why:
      '--code-bg is dark in both themes, so there is exactly one code surface and ' +
      '_prism.scss tints it from tokens. A second stylesheet would have to be swapped ' +
      'on a theme change, and the swap paints a light-styled first frame. Neither of ' +
      'Prism’s themes matches the design palette either — change _prism.scss, not this.',
    exempt: [],
  },
  {
    // Writes only: a read leaves nothing on a shared machine, and index.html's pre-paint
    // script reads the theme.
    pattern: /\blocalStorage\s*\.\s*setItem\b/,
    label: 'a direct localStorage write',
    why:
      'After sign-out localStorage must hold the theme and sidebar ' +
      'preferences and nothing else. Route writes through shared/utils/storage/' +
      'local-preferences.ts so the surviving keys are enumerated in one reviewable ' +
      'place rather than spread across call sites.',
    exempt: [
      'app/shared/utils/storage/local-preferences.ts',
      // Asserts the wrapper itself, so it has to reach past it.
      'app/shared/utils/storage/local-preferences.spec.ts',
      // Plants a disallowed key to prove sign-out does not sweep it; this check, not a
      // runtime cleanup, is the enforcement point.
      'app/components/shell/sidebar/user-footer.spec.ts',
    ],
  },
  {
    // No exemption for a block that also sets a box-shadow: that would pass a translucent
    // wash in any colour. Matches every spelling (`outline: 0px`, `outline : none`) and longhand.
    pattern: /(^|[;{\s])outline(-width|-style)?\s*:\s*(none|0[a-z%]*)\s*(!important)?\s*[;}]/,
    label: 'a suppressed focus indicator',
    escapable: true,
    why:
      'WCAG 2.1 SC 1.4.11: every interactive element needs a visible ' +
      'focus indicator at 3:1. `_focus.scss` draws one from --focus-ring for ' +
      'anything with no rule of its own; a rule that removes it must draw its own ' +
      `— an inset box-shadow for a full-bleed row — and say so with a \`${ESCAPE}\` comment.`,
    exempt: [],
  },
  {
    // --ring is ringpulse's 45%-alpha colour; as a focus indicator it composites to about
    // 1.4:1 on --surface.
    pattern: /var\(--ring\)/,
    label: 'the pulse colour used outside its keyframe',
    escapable: true,
    why:
      '--ring exists for `ringpulse` and is translucent by design. The ' +
      'focus indicator is --focus-ring, which check-tokens.mjs measures against ' +
      'every surface it can land on; --ring is measured against nothing.',
    exempt: ['styles/_motion.scss'],
  },
  {
    // A whole-number max beside `min-width: 768px` leaves a 767.5px viewport (Windows display
    // scaling) in neither mode. Component-local reflow points (480px, 575px) stay allowed.
    pattern: /\((?:max|min)-width\s*:\s*(?:767(?:\.98)?|768|1023(?:\.98)?|1024)px/,
    label: 'a hardcoded application breakpoint',
    why:
      'The three viewport modes are stated once, in styles/_breakpoints.scss ' +
      'and app/shared/utils/layout/breakpoints.ts, and check-tokens.mjs keeps the two in ' +
      'step. Use the `bp.mobile` / `bp.tablet` / `bp.desktop` mixins in a stylesheet, ' +
      'or MOBILE_VIEWPORT / TABLET_VIEWPORT in TypeScript.',
    exempt: ['styles/_breakpoints.scss', 'app/shared/utils/layout/breakpoints.ts'],
  },
  {
    // `_motion.scss` is not exempt. The word boundary stops `@keyframes spinner` passing as `spin`.
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

// ngx-markdown's README adds a Prism theme through angular.json's `styles`, which is outside
// src/ and every other gate.
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
