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
];

const findings = [];

for (const file of walk(SRC)) {
  const rel = relative(SRC, file).replaceAll('\\', '/');
  const source = readFileSync(file, 'utf8');
  // .scss and .ts share C-style comments; .html has its own form.
  const scannable = stripComments(source, { html: rel.endsWith('.html') });
  const lines = scannable.split('\n');

  for (const { pattern, label, why, exempt } of FORBIDDEN) {
    if (exempt.includes(rel)) continue;
    lines.forEach((line, index) => {
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
