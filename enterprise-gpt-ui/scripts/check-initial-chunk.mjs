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

/**
 * Code that must never be statically imported into the initial graph.
 *
 * `roots` names the other graphs it must also stay out of, and `expect` says the
 * build has to contain it *somewhere* — see the two traversals below.
 */
const FORBIDDEN = [
  {
    label: 'diagrams (US-605)',
    match: /(^|\/)node_modules\/(mermaid|@mermaid-js\/[^/]+)\//,
    roots: ['src/app/features/chat/chat.ts'],
    expect: true,
  },
  {
    label: 'math (US-605)',
    match: /(^|\/)node_modules\/(katex|mathjax|mathjax-full)\//,
    roots: ['src/app/features/chat/chat.ts'],
    expect: true,
  },
  // US-601 resolved the budget question by putting the transcript renderer behind
  // the lazy chat route rather than by raising the ceiling: the initial graph had
  // ~11 kB of headroom under its warning line and this stack is an order of
  // magnitude more than that. `provideChatMarkdown()` is therefore provided at the
  // `Chat` component, and one static import from anywhere eagerly reachable would
  // silently undo that — which is what this entry catches.
  {
    label: 'markdown renderer (US-601)',
    match: /(^|\/)node_modules\/(ngx-markdown|marked|prismjs|dompurify)\//,
  },
  // US-203 is a statement about the network, not about visibility: the admin chunk
  // must not be *requested* by a non-administrator. A `canMatch` guard is what keeps
  // it off the wire, but one stray static import from a shared module would put it in
  // the initial graph and hand it to everyone before any guard ran.
  { label: 'admin area (US-203)', match: /(^|\/)src\/app\/features\/admin\// },
  // US-1405. `axe-core` is a devDependency of roughly 600 kB, reached only from
  // `*.a11y.spec.ts` through `src/testing/a11y.ts`. The initial graph has well under a
  // kilobyte of headroom, so an accidental application import would not merely regress
  // the budget — it would ship an accessibility auditor to every user. Nothing else here
  // would catch it: a spec-only dependency is invisible to the budgets, which measure the
  // build rather than the test target.
  { label: 'the axe auditor (US-1405)', match: /(^|\/)node_modules\/axe-core\// },
  // Browser telemetry is reached only through `await import()` behind a `config.json`
  // block most deployments leave out. The SDK is several times the initial graph's
  // headroom, so one static import would regress the budget for every visitor —
  // including the ones whose deployment sends no telemetry at all. `expect` catches the
  // opposite failure: a chunk that has quietly stopped being built.
  {
    label: 'browser telemetry',
    match: /(^|\/)node_modules\/@microsoft\/applicationinsights[^/]*\//,
    expect: true,
  },
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

/**
 * Mirrors the builder's own initial-file traversal, so "initial" here means exactly
 * what the `initial` budget means: follow static imports, stop at every dynamic one.
 */
function staticGraph(from) {
  const reached = new Set([from]);
  const queue = [from];
  while (queue.length > 0) {
    const file = queue.pop();
    for (const dependency of outputs[file]?.imports ?? []) {
      const isStatic = dependency.kind === 'import-statement' || dependency.kind === 'import-rule';
      if (isStatic && outputs[dependency.path] && !reached.has(dependency.path)) {
        reached.add(dependency.path);
        queue.push(dependency.path);
      }
    }
  }
  return reached;
}

/** The chunk that owns `input`, whichever one esbuild put it in. */
function chunkOwning(input) {
  return Object.keys(outputs).find((file) => outputs[file]?.inputs?.[input] !== undefined);
}

const initial = staticGraph(entry);

const violations = [];

function scan(graph, rules, origin) {
  for (const file of graph) {
    for (const input of Object.keys(outputs[file]?.inputs ?? {})) {
      const hit = rules.find(({ match }) => match.test(input));
      if (hit) violations.push({ label: hit.label, input, file, origin });
    }
  }
}

scan(initial, FORBIDDEN, 'src/main.ts');

// A library can satisfy the rule above and still be paid for by everyone: a static
// `import 'mermaid'` from the chat route lands in the *lazy* chunk, off the initial
// graph and green here, yet downloaded by every user who opens a conversation. Each
// rule therefore names the other roots it must stay out of.
for (const rule of FORBIDDEN.filter(({ roots }) => roots?.length)) {
  for (const root of rule.roots) {
    const chunk = chunkOwning(root);
    if (chunk === undefined) {
      die(`Could not find the chunk owning ${root}; the build layout has changed.`);
    }
    scan(staticGraph(chunk), [rule], root);
  }
}

if (violations.length > 0) {
  die(
    'These libraries must be loaded on demand, but they are reachable through',
    'static imports:',
    '',
    ...violations.flatMap(({ label, input, file, origin }) => [
      `${label}`,
      `  reached from: ${origin}`,
      `  input:        ${input}`,
      `  chunk:        ${file}`,
      '',
    ]),
    'Import them with `await import(…)` from the code that needs them, so',
    'esbuild splits them into their own chunk.',
  );
}

// The checks above all pass when a library is simply absent from the build — so the
// day someone deletes the dynamic import, this would report OK and quietly stop
// testing anything. `expect` says the opposite: it has to be here, and it has to be
// somewhere lazy.
const missing = [];
for (const { label, match, expect } of FORBIDDEN.filter((rule) => rule.expect)) {
  const found = Object.keys(outputs).some((file) =>
    Object.keys(outputs[file]?.inputs ?? {}).some((input) => match.test(input)),
  );
  if (expect && !found) missing.push(label);
}

if (missing.length > 0) {
  die(
    'These libraries are not in the build at all:',
    '',
    ...missing.map((label) => `  ${label}`),
    '',
    'US-605 loads them with `await import(…)` at the moment the content appears.',
    'If that import has been removed, remove the `expect` flag with it — otherwise',
    'this check is passing because there is nothing left to check.',
  );
}

console.log(
  `initial chunk OK — ${initial.size} chunks reachable from src/main.ts, none carrying ` +
    `${FORBIDDEN.map(({ label }) => label).join(', ')}`,
);
