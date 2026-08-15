// @ts-check
// Flat config. `.mjs` rather than `.js` because package.json has no `"type":
// "module"`, so a `.js` config would be parsed as CommonJS and every `import` here
// would be a syntax error.
import js from '@eslint/js';
import ngrx from '@ngrx/eslint-plugin/v9';
import angular from 'angular-eslint';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';

/**
 * There is no permitted call site for bypassSecurityTrustHtml anywhere in this app.
 * ngx-markdown performs the trust internally, after DOMPurify has run, so any call
 * here is a second and unsanitised path to innerHTML — and model output is
 * attacker-influenceable through uploaded documents and MCP tool results.
 * US-108 / US-601.
 */
const NO_BYPASS =
  'bypassSecurityTrustHtml is banned app-wide. ngx-markdown performs the trust ' +
  'internally; there is no permitted call site. Render through the markdown ' +
  'pipeline instead.';

/**
 * One `no-restricted-imports` pattern banning a set of layers.
 *
 * Two spellings per alias: the path alias a source file actually uses, and a
 * globstar-prefixed form that also catches a relative import a refactor left behind.
 *
 * @param aliases The path aliases this layer may not import.
 */
function layerBan(aliases) {
  return {
    group: aliases.flatMap((alias) => [alias, `**/${alias.replace('@', '')}`]),
    message:
      'Dependencies run one way: features → shared → core → domain. Move the shared piece down a layer, or invert it with an injection token (see COMPOSER_HOST).',
  };
}

export default defineConfig([
  globalIgnores([
    'dist/',
    'coverage/',
    'out-tsc/',
    '.angular/',
    'public/icons/',
    // The application document, not an Angular template. The template parser and
    // the a11y rules have nothing correct to say about it.
    'src/index.html',
    // Vendored byte-for-byte from a NuGet package (US-107). Linting it would at best
    // be noise and at worst produce the very drift `npm run check:contract` exists
    // to catch.
    'src/app/domain/stream/andes/assistant-ui.contract.ts',
  ]),

  // Build and check scripts. Node globals, no type information, no Angular rules.
  {
    files: ['scripts/**/*.mjs', 'eslint.config.mjs'],
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
      // Deliberately not recommendedTypeChecked/strict/stylistic: the no-unsafe-*
      // family would light up to-app-error*.ts and the Record<string, unknown> spec
      // fixtures, which is its own story with its own remediation budget.
      ...tseslint.configs.recommended,
      ...angular.configs.tsRecommended,
      // signalsTypeChecked, not signals: with-state-no-arrays-at-root-level exists
      // only in the type-checked preset and calls getParserServices()
      // unconditionally, so it throws without type information. Spread inside a
      // files-scoped block because the ngrx configs carry a TypeScript parser and no
      // `files` of their own, and would otherwise apply it to .html and to this file.
      ...ngrx.configs.signalsTypeChecked.map((config) => ({ ...config, files: ['**/*.ts'] })),
    ],
    // Applies the template rules below to inline `template:` strings too.
    processor: angular.processInlineTemplates,
    languageOptions: {
      parserOptions: {
        // projectService rather than `project`: tsconfig.json is a solution file
        // (`files: []` plus references), and the service routes each file through
        // the reference that owns it instead of parsing both projects twice.
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // tsconfig's noUnusedLocals/noUnusedParameters own this. They catch the same
      // class of mistake on every build and every test rather than only under
      // `npm run lint`, and unlike this rule they count a `{@link}` in a doc comment
      // as a use — `with-reset-on-sign-out.ts` imports `sessionEvents` purely to
      // link to it.
      '@typescript-eslint/no-unused-vars': 'off',
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'app', style: 'kebab-case' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      // component-class-suffix and directive-class-suffix stay off: this workspace
      // uses the v21 suffix-free convention (foo.ts holding `class Foo`), which the
      // CLI's own schematics now generate.
      'no-restricted-syntax': [
        'error',
        // sanitizer.bypassSecurityTrustHtml(…), a bare or destructured call, and an
        // import of the name. The member property is an Identifier node too.
        { selector: "Identifier[name='bypassSecurityTrustHtml']", message: NO_BYPASS },
        // sanitizer['bypassSecurityTrustHtml'](…). Narrowed to a computed member so
        // that a string merely mentioning the method — a test name, an error
        // message — is not a violation.
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
    // The layering rule, made machine-checkable.
    //
    // Dependencies run one way — `features → shared → core → domain` — and until now
    // nothing enforced it. US-906 is what made that expensive: the composer had to be
    // reachable from two features, and the fix was to move it into `shared/` behind a
    // token. A single `@features/…` import from `shared/` or `core/` would undo that
    // silently, because it type-checks, builds, and only shows up as a lazy chunk the
    // whole app suddenly downloads.
    //
    // Three blocks, not one, because the rule has three edges: `domain/` is meant to be
    // framework-free so the SSE codec and the activity fold test in Node with no
    // `TestBed`, and a `core → shared` import is as much a break as the one this was
    // written for.
    //
    // Specs are exempt on purpose: `shared/composer/composer.spec.ts` drives the real
    // `TurnStore`, because what it verifies — the Send/Stop morph, the seed handoff and
    // the focus fixups — are reactions to state a stub would have to fake into
    // existence. A test file is not part of the application graph.
    files: ['src/app/shared/**/*.ts'],
    ignores: ['**/*.spec.ts'],
    rules: { 'no-restricted-imports': ['error', { patterns: [layerBan(['@features/*'])] }] },
  },

  {
    files: ['src/app/core/**/*.ts'],
    ignores: ['**/*.spec.ts'],
    rules: {
      'no-restricted-imports': ['error', { patterns: [layerBan(['@features/*', '@shared/*'])] }],
    },
  },

  {
    files: ['src/app/domain/**/*.ts'],
    ignores: ['**/*.spec.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        { patterns: [layerBan(['@features/*', '@shared/*', '@core/*'])] },
      ],
    },
  },

  {
    files: ['**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
  },
]);
