#!/usr/bin/env node
// Fails when a library that must stay lazy has landed in the initial graph. A `bundle` budget
// can't: a chunk from `import('mermaid')` has no name it matches, so it always sums zero bytes.
import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const STATS = join(UI_ROOT, 'dist', 'enterprise-gpt-ui', 'stats.json');

/**
 * Code that must never be statically imported into the initial graph. `roots` names other graphs
 * it must also stay out of; `expect` requires the build to contain it somewhere.
 */
const FORBIDDEN = [
  {
    label: 'diagrams',
    match: /(^|\/)node_modules\/(mermaid|@mermaid-js\/[^/]+)\//,
    roots: ['src/app/features/chat/chat.ts'],
    expect: true,
  },
  {
    label: 'math',
    match: /(^|\/)node_modules\/(katex|mathjax|mathjax-full)\//,
    roots: ['src/app/features/chat/chat.ts'],
    expect: true,
  },
  // Provided at `Chat` so it stays behind the lazy route: the initial graph has ~11 kB of
  // headroom and this stack is an order of magnitude more.
  {
    label: 'markdown renderer',
    match: /(^|\/)node_modules\/(ngx-markdown|marked|prismjs|dompurify)\//,
  },
  // Non-administrators must never download the admin chunk; `canMatch` keeps it off the wire
  // only while nothing eagerly reachable imports it statically.
  { label: 'admin area', match: /(^|\/)src\/app\/features\/admin\// },
  // Features load with their route; the auth pages are the listed eager exceptions (the sign-in
  // callback and the failure routes). The core widgets and the shared components that only lazy
  // features render ride with them.
  {
    label: 'feature code',
    match:
      /(^|\/)src\/app\/(features\/(?!auth\/)|core\/(composer|uploads)\/|core\/projects\/project-conversations-store\.ts|shared\/(badges\/(permission|kind)-badge|cards\/project-card)\.)/,
  },
  // A ~600 kB devDependency reached only from `*.a11y.spec.ts`; the budgets measure the build,
  // not the test target, so nothing else would catch it shipping to every user.
  { label: 'the axe auditor', match: /(^|\/)node_modules\/axe-core\// },
  // Imported dynamically only when `config.json` enables it; the SDK is several times the
  // initial graph's headroom.
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

/** Mirrors the builder's initial-file traversal: follow static imports, stop at dynamic ones. */
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

// A static import from the chat route passes the scan above yet still downloads for everyone
// who opens a conversation, so each rule's `roots` are scanned too.
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

// The checks above pass when a library is absent, so a deleted dynamic import would leave them
// testing nothing.
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
    'They are imported dynamically: diagrams and math when the content appears,',
    'telemetry when `config.json` enables it.',
    'If that import has been removed, remove the `expect` flag with it — otherwise',
    'this check is passing because there is nothing left to check.',
  );
}

console.log(
  `initial chunk OK — ${initial.size} chunks reachable from src/main.ts, none carrying ` +
    `${FORBIDDEN.map(({ label }) => label).join(', ')}`,
);
