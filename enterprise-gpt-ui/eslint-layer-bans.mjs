// @ts-check
// ESLint flat-config blocks that enforce the dependency direction of the angular-ui-architecture skill:
//   pages → components/<feature> → shared/components|directives|pipes → state → services → core → shared/utils → shared/models
// ESM port of the skill's assets/eslint-layer-bans.js.
// Options: { srcDir: 'src', banForms: false, skill: 'angular-ui-architecture' }
//   banForms: true once the app is on Signal Forms — bans FormsModule / ReactiveFormsModule imports.
import fs from 'node:fs';
import path from 'node:path';

const folder = (name) => [`@${name}/*`, `**/app/${name}/*`];

// Negate the directory itself: a gitignore matcher cannot re-include a child of an excluded directory.
const except = (name) => [`!@${name}`, `!**/app/${name}`];

const presentational = [
  ...folder('shared/components'),
  ...folder('shared/directives'),
  ...folder('shared/pipes'),
];

const allOfApp = [
  ...folder('pages'),
  ...folder('components'),
  ...folder('state'),
  ...folder('services'),
  ...folder('core'),
  ...folder('shared'),
];

/** Top-level folders of `src/app/<kind>`: the feature names a per-feature ban is generated for. */
const foldersIn = (rootDir, src, kind) => {
  const dir = path.join(rootDir, src, 'app', kind);
  return fs.existsSync(dir)
    ? fs
        .readdirSync(dir, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name)
    : [];
};

/**
 * The ban table again, as `import-x/no-restricted-paths` zones. Not in the skill's asset: zones
 * resolve every import, so they also see a dynamic `import('@pages/…')`, which
 * `no-restricted-imports` never inspects.
 * @param {string} rootDir the workspace root; pass it as the rule's `basePath` too
 * @param {{ srcDir?: string, skill?: string }} [options]
 */
export function layerZones(rootDir, { srcDir = 'src', skill = 'angular-ui-architecture' } = {}) {
  const src = srcDir.replace(/\/$/, '');
  const dir = (name) => `./${src}/app/${name}`;
  const message = `Crosses a layer boundary; see the ${skill} skill.`;
  const zone = (target, from, except) => ({ target, from, ...(except && { except }), message });
  const presentational = ['shared/components', 'shared/directives', 'shared/pipes'].map(dir);

  return [
    zone(dir('shared/models'), [
      ...['pages', 'components', 'state', 'services', 'core', 'shared/utils'].map(dir),
      ...presentational,
    ]),
    zone(dir('shared/utils'), [
      ...['pages', 'components', 'state', 'services', 'core'].map(dir),
      ...presentational,
    ]),
    zone(dir('core'), [
      ...presentational,
      ...['services', 'state', 'components', 'pages'].map(dir),
    ]),
    zone(dir('services'), [...presentational, ...['state', 'components', 'pages'].map(dir)]),
    zone(dir('state'), [...presentational, ...['components', 'pages'].map(dir)]),
    zone(presentational, [
      ...foldersIn(rootDir, src, 'state').map((feature) => dir(`state/${feature}`)),
      dir('components'),
      dir('pages'),
    ]),
    ...foldersIn(rootDir, src, 'components').flatMap((feature) => [
      zone(dir(`components/${feature}`), dir('pages')),
      zone(dir(`components/${feature}`), dir('components'), [`./${feature}`]),
    ]),
    ...foldersIn(rootDir, src, 'pages').map((feature) =>
      zone(dir(`pages/${feature}`), dir('pages'), [`./${feature}`]),
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

  // Flat config replaces a rule's options when blocks overlap, so each layer block repeats the forms ban.
  const layer = (files, group) => ({
    files,
    ignores: ['**/*.spec.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        { paths, patterns: [{ group, message: `Crosses a layer boundary; ${see}.` }] },
      ],
    },
  });

  const featuresOf = (kind) => {
    const dir = path.join(rootDir, src, 'app', kind);
    return fs.existsSync(dir)
      ? fs
          .readdirSync(dir, { withFileTypes: true })
          .filter((entry) => entry.isDirectory())
          .map((entry) => entry.name)
      : [];
  };

  return [
    ...(banForms
      ? [{ files: [`${src}/**/*.ts`], rules: { 'no-restricted-imports': ['error', { paths }] } }]
      : []),
    layer(
      [`${src}/app/shared/models/**/*.ts`],
      ['@angular/*', '@ngrx/*', ...allOfApp, ...except('shared/models')],
    ),
    layer(
      [`${src}/app/shared/utils/**/*.ts`],
      ['@angular/*', '@ngrx/*', ...allOfApp, ...except('shared/models'), ...except('shared/utils')],
    ),
    layer(
      [`${src}/app/core/**/*.ts`],
      [
        ...presentational,
        ...folder('services'),
        ...folder('state'),
        ...folder('components'),
        ...folder('pages'),
      ],
    ),
    layer(
      [`${src}/app/services/**/*.ts`],
      [...presentational, ...folder('state'), ...folder('components'), ...folder('pages')],
    ),
    layer(
      [`${src}/app/state/**/*.ts`],
      [...presentational, ...folder('components'), ...folder('pages')],
    ),
    layer(
      [`${src}/app/shared/{components,directives,pipes}/**/*.ts`],
      ['@state/*/*', '**/app/state/*/*', ...folder('components'), ...folder('pages')],
    ),
    ...featuresOf('components').map((feature) =>
      layer(
        [`${src}/app/components/${feature}/**/*.ts`],
        [...folder('pages'), ...folder('components'), ...except(`components/${feature}`)],
      ),
    ),
    ...featuresOf('pages').map((feature) =>
      layer(
        [`${src}/app/pages/${feature}/**/*.ts`],
        [...folder('pages'), ...except(`pages/${feature}`)],
      ),
    ),
  ];
}
