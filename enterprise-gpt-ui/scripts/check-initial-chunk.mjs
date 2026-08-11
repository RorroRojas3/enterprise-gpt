#!/usr/bin/env node
// Fails when a library that must stay lazy has landed in the initial graph.
// US-108 / FR-46.
//
// A `bundle`-type budget cannot do this. `generateBudgetStats` names a chunk only
// from `initialFiles` or from `metafile.outputs[…].entryPoint`, and a chunk produced
// by `import('mermaid')` has neither — so the budget sums zero bytes and can never
// fail. The `initial` budget still guards *size*; this guards *identity*, and says
// which library and which import path put it there.
import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const STATS = join(UI_ROOT, 'dist', 'enterprise-gpt-ui', 'stats.json');

/** Code that must never be statically imported into the initial graph. */
const FORBIDDEN = [
  { label: 'diagrams (US-605)', match: /(^|\/)node_modules\/(mermaid|@mermaid-js\/[^/]+)\// },
  { label: 'math (US-605)', match: /(^|\/)node_modules\/(katex|mathjax|mathjax-full)\// },
  // US-203 is a statement about the network, not about visibility: the admin chunk
  // must not be *requested* by a non-administrator. A `canMatch` guard is what keeps
  // it off the wire, but one stray static import from a shared module would put it in
  // the initial graph and hand it to everyone before any guard ran.
  { label: 'admin area (US-203)', match: /(^|\/)src\/app\/features\/admin\// },
];

function die(...lines) {
  console.error('\nInitial chunk check FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

if (!existsSync(STATS)) {
  die(
    `Build statistics not found: ${STATS}`,
    '',
    'Run a production build first (`npm run build`), and check that',
    '`statsJson: true` is still set on the production configuration in angular.json.',
  );
}

const { outputs } = JSON.parse(readFileSync(STATS, 'utf8'));

const entry =
  Object.keys(outputs).find((file) => outputs[file].entryPoint === 'src/main.ts') ??
  Object.keys(outputs).find((file) => /(^|\/)main[-.][^/]*\.js$/.test(file));
if (!entry) die('Could not find the src/main.ts entry point in the build metafile.');

// Mirrors the builder's own initial-file traversal, so "initial" here means exactly
// what the `initial` budget means: follow static imports, stop at every dynamic one.
const initial = new Set([entry]);
const queue = [entry];
while (queue.length > 0) {
  const file = queue.pop();
  for (const dependency of outputs[file]?.imports ?? []) {
    const isStatic = dependency.kind === 'import-statement' || dependency.kind === 'import-rule';
    if (isStatic && outputs[dependency.path] && !initial.has(dependency.path)) {
      initial.add(dependency.path);
      queue.push(dependency.path);
    }
  }
}

const violations = [];
for (const file of initial) {
  for (const input of Object.keys(outputs[file]?.inputs ?? {})) {
    const hit = FORBIDDEN.find(({ match }) => match.test(input));
    if (hit) violations.push({ label: hit.label, input, file });
  }
}

if (violations.length > 0) {
  die(
    'These libraries must be loaded on demand, but they are reachable from',
    'src/main.ts through static imports:',
    '',
    ...violations.flatMap(({ label, input, file }) => [
      `${label}`,
      `  input:  ${input}`,
      `  chunk:  ${file}`,
      '',
    ]),
    'Import them with `await import(…)` from the component that needs them, so',
    'esbuild splits them into their own chunk.',
  );
}

console.log(
  `initial chunk OK — ${initial.size} chunks reachable from src/main.ts, none carrying ` +
    `${FORBIDDEN.map(({ label }) => label).join(', ')}`,
);
