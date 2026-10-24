// @ts-check
// ESLint flat-config blocks that enforce the dependency direction of the angular-ui-architecture skill:
//   features/<feature> → core | shared; core → shared; shared → *.model.ts only; features never import each other;
//   *.model.ts → other *.model.ts only.
// ESM port of the skill's assets/eslint-layer-bans.js, plus layerZones() below.
// Options: { srcDir: 'src', banForms: false, skill: 'angular-ui-architecture' }
//   banForms: true once the app is on Signal Forms — bans FormsModule / ReactiveFormsModule imports.
import fs from 'node:fs';
import path from 'node:path';

const folder = (name) => [`@${name}/*`, `**/app/${name}/*`];

// Negate the directory itself: a gitignore matcher cannot re-include a child of an excluded directory.
const except = (name) => [`!@${name}`, `!**/app/${name}`];

// Nor can it re-include files by name inside an excluded folder, so "except models" needs a regex.
const coreExceptModels = String.raw`^(@core/|(.*/)?app/core/)(?!.*\.model$)`;
const anyAppExceptModels = String.raw`^(@(core|shared|features)/|(.*/)?app/(core|shared|features)/|\.{1,2}/)(?!.*\.model$)`;

/** Top-level folders of `src/app/features`: the features a per-feature ban is generated for. */
const featuresIn = (rootDir, src) => {
  const dir = path.join(rootDir, src, 'app', 'features');
  return fs.existsSync(dir)
    ? fs
        .readdirSync(dir, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name)
    : [];
};

/**
 * The feature bans again, as `import-x/no-restricted-paths` zones. Not in the skill's asset: zones
 * resolve every import, so they also see a dynamic `import('@features/…')`, which
 * `no-restricted-imports` never inspects.
 * @param {string} rootDir the workspace root; pass it as the rule's `basePath` too
 * @param {{ srcDir?: string, skill?: string }} [options]
 */
export function layerZones(rootDir, { srcDir = 'src', skill = 'angular-ui-architecture' } = {}) {
  const src = srcDir.replace(/\/$/, '');
  const dir = (name) => `./${src}/app/${name}`;
  const message = `Crosses a layer boundary; see the ${skill} skill.`;
  const zone = (target, from, except) => ({ target, from, ...(except && { except }), message });

  return [
    zone(dir('core'), dir('features')),
    zone(dir('shared'), dir('features')),
    ...featuresIn(rootDir, src).map((feature) =>
      zone(dir(`features/${feature}`), dir('features'), [`./${feature}`]),
    ),
  ];
}

/**
 * @param {string} rootDir the workspace root (usually `import.meta.dirname` of eslint.config.mjs)
 * @param {{ srcDir?: string, banForms?: boolean, skill?: string }} [options]
 */
export function layerBans(
  rootDir,
  { srcDir = 'src', banForms = false, skill = 'angular-ui-architecture' } = {},
) {
  const see = `see the ${skill} skill`;
  const message = `Crosses a layer boundary; ${see}.`;
  const src = srcDir.replace(/\/$/, '');
  const paths = banForms
    ? [
        {
          name: '@angular/forms',
          importNames: ['FormsModule', 'ReactiveFormsModule'],
          message: `Use Signal Forms; ${see}.`,
        },
      ]
    : [];

  // Flat config replaces a rule's options when blocks overlap, so each block repeats the forms ban.
  const block = (files, ignores, group, regexes) => ({
    files,
    ignores,
    rules: {
      'no-restricted-imports': [
        'error',
        {
          paths,
          patterns: [
            ...(group.length ? [{ group, message }] : []),
            ...regexes.map((regex) => ({ regex, message })),
          ],
        },
      ],
    },
  });

  // A folder's models get a block of their own that keeps the folder's bans and adds the model bans,
  // because the later of two blocks matching one file would otherwise drop the earlier one's.
  const layer = (dir, { group = [], regexes = [] }) => [
    block([`${dir}/**/*.ts`], ['**/*.spec.ts', '**/*.model.ts'], group, regexes),
    block(
      [`${dir}/**/*.model.ts`],
      ['**/*.spec.ts'],
      ['@angular/*', '@ngrx/*', ...group],
      [anyAppExceptModels],
    ),
  ];

  const app = `${src}/app`;
  return [
    ...(banForms
      ? [{ files: [`${src}/**/*.ts`], rules: { 'no-restricted-imports': ['error', { paths }] } }]
      : []),
    block([`${app}/*.model.ts`], ['**/*.spec.ts'], ['@angular/*', '@ngrx/*'], [anyAppExceptModels]),
    ...layer(`${app}/shared`, { group: folder('features'), regexes: [coreExceptModels] }),
    ...layer(`${app}/core`, { group: folder('features') }),
    ...featuresIn(rootDir, src).flatMap((feature) =>
      layer(`${app}/features/${feature}`, {
        group: [...folder('features'), ...except(`features/${feature}`)],
      }),
    ),
  ];
}
