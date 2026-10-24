#!/usr/bin/env node
// Checks the vendored Andes contract matches its NuGet package byte-for-byte, pins the version
// the API references, and has no hand-written duplicates under src/.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const UI_ROOT = resolve(fileURLToPath(import.meta.url), '../..');
const REPO_ROOT = resolve(UI_ROOT, '..');
const API_ROOT = join(REPO_ROOT, 'enterprise-gpt-api');
const VENDORED = join(UI_ROOT, 'src', 'app', 'core', 'api', 'assistant-ui-contract.ts');
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

/** Escapes every regex metacharacter in one pass, backslash included. */
function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

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
// Tolerates CRLF despite the strict comparison below, so an EOL problem reports as a byte
// difference with a hint rather than as a missing sentinel.
if (body.startsWith('\r\n')) body = body.slice(2);
else if (body.startsWith('\n')) body = body.slice(1);

// The header is the only place the version is pinned.
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

// A byte comparison against the old package folder still passes after the API upgrades.
if (existsSync(API_ROOT)) {
  const projects = [];
  const walkProjects = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (!/^(bin|obj|\.vs|node_modules|TestResults)$/i.test(entry.name)) {
          walkProjects(join(dir, entry.name));
        }
      } else if (entry.name.endsWith('.csproj') || entry.name === 'Directory.Packages.props') {
        projects.push(join(dir, entry.name));
      }
    }
  };
  walkProjects(API_ROOT);

  // Central package management moves the version from the csproj's PackageReference to a
  // PackageVersion in Directory.Packages.props; either form declares it.
  const reference = new RegExp(
    `Package(?:Reference|Version)\\s+Include="${escapeRegExp(packageId)}"\\s+Version="([^"]+)"`,
    'i',
  );
  const declared = projects
    .map((file) => [file, readFileSync(file, 'utf8').match(reference)?.[1]])
    .filter(([, referenced]) => referenced);

  if (declared.length === 0) {
    die(
      `No csproj or Directory.Packages.props under the API declares a version for ${packageId}.`,
      `searched: ${API_ROOT}`,
      '',
      'The version cross-check would otherwise pass having verified nothing.',
    );
  }

  const mismatched = declared.filter(([, referenced]) => referenced !== version);

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
