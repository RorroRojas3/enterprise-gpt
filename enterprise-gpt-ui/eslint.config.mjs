// @ts-check
// `.mjs` because package.json has no `"type": "module"`; a `.js` config would parse as CommonJS.
import js from '@eslint/js';
import ngrx from '@ngrx/eslint-plugin/v9';
import angular from 'angular-eslint';
import { createTypeScriptImportResolver } from 'eslint-import-resolver-typescript';
import { createNodeResolver, importX } from 'eslint-plugin-import-x';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import { layerBans, layerZones } from './eslint-layer-bans.mjs';

/**
 * ngx-markdown trusts HTML itself after DOMPurify, so any other call is an unsanitised innerHTML
 * path for model output, which uploaded documents and MCP tool results can influence.
 */
const NO_BYPASS =
  'bypassSecurityTrustHtml is banned app-wide. ngx-markdown performs the trust ' +
  'internally; there is no permitted call site. Render through the markdown ' +
  'pipeline instead.';

export default defineConfig([
  globalIgnores([
    'dist/',
    'coverage/',
    'out-tsc/',
    '.angular/',
    'public/icons/',
    // The host document, not an Angular template.
    'src/index.html',
    // Vendored byte-for-byte from a NuGet package; lint fixes would be drift.
    'src/app/core/api/assistant-ui-contract.ts',
  ]),

  {
    files: ['scripts/**/*.mjs', 'eslint.config.mjs', 'eslint-layer-bans.mjs'],
    extends: [js.configs.recommended],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: globals.node,
    },
  },

  {
    files: ['**/*.ts'],
    extends: [
      js.configs.recommended,
      // Not recommendedTypeChecked: no-unsafe-* would flag to-app-error*.ts and the
      // Record<string, unknown> spec fixtures, a separate cleanup.
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
      // Type-checked for with-state-no-arrays-at-root-level. Scoped to *.ts because the ngrx
      // configs carry a TS parser and no `files`, so would otherwise hit .html and this file.
      ...ngrx.configs.signalsTypeChecked.map((config) => ({ ...config, files: ['**/*.ts'] })),
    ],
    processor: angular.processInlineTemplates,
    languageOptions: {
      parserOptions: {
        // tsconfig.json is a solution file; the service resolves each file through the
        // reference that owns it instead of parsing both projects.
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // tsconfig's noUnusedLocals/noUnusedParameters own this: they run on every build and,
      // unlike this rule, count a `{@link}` in a doc comment as a use.
      '@typescript-eslint/no-unused-vars': 'off',
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'app', style: 'kebab-case' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      // component-/directive-class-suffix stay off: v21 names are suffix-free (`class Foo`).
      'no-restricted-syntax': [
        'error',
        // Member, bare, destructured and imported uses; a member property is an Identifier too.
        { selector: "Identifier[name='bypassSecurityTrustHtml']", message: NO_BYPASS },
        // sanitizer['bypassSecurityTrustHtml'](…). Computed members only, so a test name or an
        // error message that mentions the method passes.
        {
          selector: "MemberExpression[computed=true] > Literal[value='bypassSecurityTrustHtml']",
          message: NO_BYPASS,
        },
        {
          selector:
            "MemberExpression[computed=true] > TemplateLiteral > TemplateElement[value.cooked='bypassSecurityTrustHtml']",
          message: NO_BYPASS,
        },
      ],
    },
  },

  {
    // `no-restricted-imports` matches the specifier text of static imports only, so a `../` import
    // or a dynamic `import('@features/…')` slips past the layer bans; this block closes both gaps.
    files: ['src/app/**/*.ts'],
    ignores: ['**/*.spec.ts'],
    plugins: { 'import-x': importX },
    settings: {
      // Without it import-x maps only `.js` modules, and `no-cycle` never reports.
      'import-x/extensions': ['.ts', '.mjs', '.js'],
      'import-x/resolver-next': [
        createTypeScriptImportResolver({ project: './tsconfig.json' }),
        createNodeResolver(),
      ],
    },
    rules: {
      'import-x/no-cycle': ['error', { ignoreExternal: true }],
      // The `ignore` keeps aliases legal; they resolve inside the repo, so count as parents.
      'import-x/no-relative-parent-imports': [
        'error',
        { ignore: ['^@(core|features|shared|testing)/'] },
      ],
      'import-x/no-restricted-paths': [
        'error',
        {
          basePath: import.meta.dirname,
          zones: [
            ...layerZones(import.meta.dirname),
            // The SSE codec and turn reducers test in Node with no TestBed; no folder rule marks them.
            {
              target: './src/app/features/chat/stream',
              from: ['./node_modules/@angular', './node_modules/@ngrx'],
              message: 'The stream codec stays framework-free so it tests in Node.',
            },
          ],
        },
      ],
    },
  },

  {
    files: ['**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
  },

  // Last: a wrong-way import type-checks and builds, surfacing only as a lazy chunk the whole app
  // downloads. Specs are exempt: they are outside the app graph and may drive real stores.
  ...layerBans(import.meta.dirname, { banForms: true }),
]);
