#!/usr/bin/env node
// Verifies the vendored Andes streaming contract still matches the installed NuGet
// package byte-for-byte, that the version it pins is the one the API actually
// references, and that nobody has hand-written a duplicate of its public symbols.
// US-107.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const REPO_ROOT = resolve(UI_ROOT, '..');
const API_ROOT = join(REPO_ROOT, 'enterprise-gpt-api');
const VENDORED = join(
  UI_ROOT,
  'src',
  'app',
  'domain',
  'stream',
  'andes',
  'assistant-ui.contract.ts',
);
const SENTINEL = '// --- END VENDOR HEADER';

/** The four symbols that may exist in exactly one place: the vendored file. */
const VENDORED_SYMBOLS = [
  'AssistantUiEvent',
  'AssistantActivity',
  'AssistantStatusSnapshot',
  'foldAssistantEvents',
];

function die(...lines) {
  console.error('\nAndes contract check FAILED\n');
  for (const line of lines) console.error('  ' + line);
  console.error('');
  process.exit(1);
}

/**
 * Escapes every regular-expression metacharacter, backslash included.
 *
 * The character class has to carry `\\` and the replacement has to be `$&`, so the
 * escaping happens in one pass — escaping `.` alone, as this did, leaves a literal
 * backslash in the value to combine with the ones being inserted. The failure is
 * silent rather than loud: the pattern stops matching, the version cross-check below
 * finds no `PackageReference`, and the script reports OK having verified nothing.
 */
function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// 1. Read the vendored file and split it at the sentinel.
if (!existsSync(VENDORED)) {
  die('The vendored contract is missing.', `expected: ${VENDORED}`);
}
const text = readFileSync(VENDORED, 'utf8');
const sentinelAt = text.indexOf(SENTINEL);
if (sentinelAt < 0) {
  die(`The vendored file has no "${SENTINEL}" line.`, `file: ${VENDORED}`);
}
const lineEnd = text.indexOf('\n', sentinelAt);
let body = text.slice(lineEnd + 1);
// One blank line separates the header from the package content. Tolerate CRLF here
// even though the comparison below is strict, so an EOL problem is reported as a
// byte difference with a useful hint rather than as a missing sentinel.
if (body.startsWith('\r\n')) body = body.slice(2);
else if (body.startsWith('\n')) body = body.slice(1);

// 2. The header is the only place the version is pinned.
const header = text.slice(0, sentinelAt);
const tag = (name) =>
  header.match(new RegExp(`@andes-contract-${escapeRegExp(name)}\\s+(\\S+)`))?.[1];
const packageId = tag('package');
const version = tag('version');
const sourcePath = tag('source');
if (!packageId || !version || !sourcePath) {
  die(
    'The vendor header must carry @andes-contract-package, -version and -source.',
    `file: ${VENDORED}`,
  );
}

// 3. Resolve the NuGet global-packages folder. os.homedir() already yields
//    %USERPROFILE% on Windows, so there is no third branch to write.
const nugetRoot = process.env['NUGET_PACKAGES'] || join(homedir(), '.nuget', 'packages');
const packageFile = join(
  nugetRoot,
  packageId.toLowerCase(),
  version.toLowerCase(),
  ...sourcePath.split('/'),
);
if (!existsSync(packageFile)) {
  die(
    'The NuGet package copy is not on this machine, so drift cannot be checked.',
    `expected: ${packageFile}`,
    `vendored: ${VENDORED}`,
    '',
    'Restore it first:',
    `  dotnet restore ${join(API_ROOT, 'Enterprise.Gpt.Service', 'Enterprise.Gpt.Service.csproj')}`,
    'or point NUGET_PACKAGES at the folder that holds it.',
  );
}

// 4. Byte-for-byte.
const packageBytes = readFileSync(packageFile);
const bodyBytes = Buffer.from(body, 'utf8');
if (!bodyBytes.equals(packageBytes)) {
  const normalize = (buffer) => Buffer.from(buffer.toString('utf8').replace(/\r\n/g, '\n'), 'utf8');
  const eolOnly = normalize(bodyBytes).equals(normalize(packageBytes));
  let offset = 0;
  while (offset < bodyBytes.length && bodyBytes[offset] === packageBytes[offset]) offset++;
  const line = body.slice(0, offset).split('\n').length;
  die(
    'The vendored contract has drifted from the installed package copy.',
    '',
    `vendored: ${VENDORED}`,
    `package:  ${packageFile}`,
    '',
    `first difference at byte ${offset} of the vendored body (line ${line})`,
    ...(eolOnly
      ? [
          '',
          'The files differ ONLY in line endings — the working-tree copy is CRLF.',
          `Check that ${join(UI_ROOT, '.gitattributes')} pins this path to eol=lf,`,
          'then delete the file and check it out again.',
        ]
      : [
          '',
          `vendored: ${JSON.stringify(body.split('\n')[line - 1] ?? '')}`,
          `package:  ${JSON.stringify(packageBytes.toString('utf8').split('\n')[line - 1] ?? '')}`,
        ]),
    '',
    'Copy the package file over everything below the END VENDOR HEADER line, or',
    'revert the vendored file. Never edit it by hand.',
  );
}

// 5. Cross-check the pinned version against the API's PackageReferences. A byte
//    comparison against the *old* package folder still passes after the API
//    upgrades, which is the failure this catches and the AC does not.
if (existsSync(API_ROOT)) {
  const projects = [];
  const walkProjects = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (!/^(bin|obj|\.vs|node_modules|TestResults)$/i.test(entry.name)) {
          walkProjects(join(dir, entry.name));
        }
      } else if (entry.name.endsWith('.csproj')) {
        projects.push(join(dir, entry.name));
      }
    }
  };
  walkProjects(API_ROOT);

  const reference = new RegExp(
    `PackageReference\\s+Include="${escapeRegExp(packageId)}"\\s+Version="([^"]+)"`,
    'i',
  );
  const mismatched = projects
    .map((file) => [file, readFileSync(file, 'utf8').match(reference)?.[1]])
    .filter(([, referenced]) => referenced && referenced !== version);

  if (mismatched.length > 0) {
    die(
      `The API references ${packageId} at a different version than the vendored contract pins.`,
      `vendored: ${version}  (${VENDORED})`,
      ...mismatched.map(([file, referenced]) => `csproj:   ${referenced}  (${file})`),
      '',
      'Re-vendor the contract from the version the API actually uses.',
    );
  }
}

// 6. No hand-written duplicate of the vendored symbols anywhere under src/.
const declaration = (symbol) =>
  new RegExp(
    `^\\s*(export\\s+)?(declare\\s+)?(interface|type|class|enum|function|const|let|var)\\s+${escapeRegExp(symbol)}\\b`,
    'm',
  );
const duplicates = [];
const walkSources = (dir) => {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      walkSources(path);
    } else if (entry.name.endsWith('.ts') && path !== VENDORED) {
      const source = readFileSync(path, 'utf8');
      for (const symbol of VENDORED_SYMBOLS) {
        if (declaration(symbol).test(source)) duplicates.push([path, symbol]);
      }
    }
  }
};
walkSources(join(UI_ROOT, 'src'));

if (duplicates.length > 0) {
  die(
    'Hand-written declarations of vendored contract symbols found. Import them from',
    `  ${VENDORED}`,
    '',
    ...duplicates.map(([file, symbol]) => `${symbol}  in  ${file}`),
  );
}

console.log(
  `andes contract OK — ${packageId} ${version}, ${packageBytes.length} bytes match ${packageFile}`,
);
