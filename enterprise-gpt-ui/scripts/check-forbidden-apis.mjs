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

console.log(`forbidden APIs OK — none of ${FORBIDDEN.length} patterns appears under src/`);
