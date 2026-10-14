# Enterprise GPT — web client

Angular 21 client for the Enterprise GPT API.

Zoneless change detection, standalone components, `@ngrx/signals` for state, Bootstrap 5.3 SCSS —
full, with the app's palette fed into its variables, and **no Bootstrap JavaScript** — MSAL for Entra ID,
and Vitest via `@angular/build:unit-test`.

Companion references: [`docs/architecture/frontend.md`](../docs/architecture/frontend.md) for
layering, bootstrap and routing, [`docs/frontend/state.md`](../docs/frontend/state.md) and
[`docs/frontend/errors.md`](../docs/frontend/errors.md) for the store features and error
normalization, and [`docs/frontend/design-system.md`](../docs/frontend/design-system.md) for tokens,
theming, assets and the shared component kit.

## Prerequisites

- Node 24 (`package.json` pins `engines.node` to `^24.0.0`, and CI reads the version from it) and
  npm 11+
- The API running on `https://localhost:7045` (`dotnet run` in
  [`../enterprise-gpt-api/Enterprise.Gpt.Api`](../enterprise-gpt-api/Enterprise.Gpt.Api))
- **`dotnet dev-certs https --trust`**, once. Without a trusted development certificate the browser
  rejects every call to the API and reports it as a CORS failure, which is misleading — the origin
  is allow-listed, the certificate is not.

The API's CORS policy allows exactly `http://localhost:4200`, so keep the dev server on its
default port.

## Commands

```bash
npm start          # ng serve on http://localhost:4200
npm run build      # production build into dist/, then the initial-chunk check
npm test           # Vitest, single run
npm run test:watch # Vitest, watch mode
npm run format     # Prettier (format:check for the read-only variant)
npm run lint       # lint:es + check:icons + check:forbidden + check:tokens
```

`npm start`, `npm run build` and `npm test` each run `npm run assets` first, so the generated
fonts and icon sprite are always present and current.

| Command                   | What it does                                                                                                                             |
| ------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `npm run lint:es`         | ESLint alone. Flat config in `eslint.config.mjs`; `--max-warnings=0`, so a warning fails                                                 |
| `npm run lint:fix`        | ESLint with `--fix`                                                                                                                      |
| `npm run check:contract`  | Compares the vendored streaming contract against the installed NuGet package, byte for byte. Needs `dotnet restore` to have run          |
| `npm run check:icons`     | Every `bi-*` name under `src/` is in the sprite manifest                                                                                 |
| `npm run check:forbidden` | Text backstop for `bypassSecurityTrustHtml`, any Google Fonts or CDN origin, Bootstrap JavaScript, and removed colour tokens             |
| `npm run check:tokens`    | `index.html`'s duplicated colours and storage key against the tokens, text, fill, tooltip and focus-ring contrast, and breakpoint parity |
| `npm run assets`          | `assets:fonts` + `assets:icons` + `assets:katex`. Runs automatically before start, build and test                                        |

The full set — format, lint, the contract check, tests and the build — is what
[`.github/workflows/ui-ci.yml`](../.github/workflows/ui-ci.yml) runs on every pull request.

## Runtime configuration

Nothing environment-specific is compiled into the bundle: there is no `environments/` directory and
no `fileReplacements` entry. `src/main.ts` fetches `config.json` with `cache: 'no-store'`, validates
it, and provides it through the `APP_CONFIG` injection token **before** `bootstrapApplication`. One
build artifact is promoted across environments and `config.json` is replaced per environment.

`public/config.json` is the committed development configuration and ships to `dist/` unhashed.

```json
{
  "apiBaseUrl": "https://localhost:7045",
  "auth": {
    "clientId": "…",
    "authority": "https://login.microsoftonline.com/{tenantId}",
    "redirectUri": "/auth",
    "postLogoutRedirectUri": "/",
    "apiScopes": ["api://{apiClientId}/access_as_user"]
  },
  "features": { "diagrams": false, "math": false, "rawStreamCodec": false }
}
```

The committed values assume the SPA reuses the API's app registration. If a separate SPA app
registration exists, replace `auth.clientId` with it.

`index.html` carries a static startup shell that is visible from first paint and removed once the
app bootstraps. If `config.json` is missing, unreachable, or fails validation, the app does not
bootstrap and the shell switches to a failure state naming the offending field and offering a retry
— never a blank page. Because the shell starts visible rather than hidden, this also covers the case
where the bundle itself never loads and no application code runs at all. To see it: rename
`public/config.json`, hard-reload, then rename it back.

## Layout

`src/app/` is grouped by **kind** first and by **feature** second, as the Andes
`angular-ui-architecture` skill defines it. A feature such as `chat` has `pages/chat/`,
`components/chat/` and `state/chat/`. Dependencies run one way only:

```text
pages → components/<feature> → shared/{components,directives,pipes} → state → services → core → shared/utils → shared/models
```

`npm run lint` enforces this direction through generated `no-restricted-imports` blocks and
`import-x/no-restricted-paths` zones, which also cover dynamic `import()` (`eslint-layer-bans.mjs`). Importing across features (`components/a` → `components/b`,
`pages/a` → `pages/b`) is banned too. `shared/utils` and `shared/models` import nothing from
Angular or NgRx, so the stream codec and the activity fold stay testable in Node with no `TestBed`.

| Path                  | Holds                                                                                                                                                                                                                                 |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/app/pages/`      | Route destinations and route tables. Every page is lazy except the auth pages. `pages/admin` is guarded with `canMatch`, so its chunk is never fetched by a non-administrator.                                                        |
| `src/app/components/` | Components that one feature renders, one folder per feature. `components/composer` is a pageless feature that the chat and project pages both compose.                                                                                |
| `src/app/state/`      | Root stores sit flat in `state/`. Stores provided on a page or route live in `state/<feature>/`.                                                                                                                                      |
| `src/app/services/`   | Streaming and upload transports, grouped by domain.                                                                                                                                                                                   |
| `src/app/core/`       | App-wide singletons and policy: runtime config, error normalization, HTTP, auth, session, navigation, theme, cross-store events, the composable signal-store features, and the stores that guards or core services inject.            |
| `src/app/shared/`     | `components/`, `directives/` and `pipes/` hold reusable building blocks. `models/` holds wire DTOs and the vendored streaming contract. `utils/` holds framework-free pure functions.                                                 |
| `src/styles/`         | Global Sass partials (`angular.json` puts this directory on the load path). Components read tokens as `var(--…)` and must **not** `@use 'tokens'`, `@use 'theme'` or `@use 'typography'`, which emit `:root` and `@font-face` blocks. |
| `src/icons/brand/`    | Brand-mark SVGs that `npm run assets:icons` folds into the sprite.                                                                                                                                                                    |
| `src/testing/`        | Test-only providers and shared fixtures. Excluded from `tsconfig.app.json`.                                                                                                                                                           |

Path aliases `@pages/*`, `@components/*`, `@state/*`, `@services/*`, `@core/*`, `@shared/*` and
`@testing/*` are configured in `tsconfig.json`. Imports cross folders through the alias, never
through `../`. Prettier sorts imports in the same bottom-up order as the graph.

Component files carry no suffix (`foo.ts` / `foo.html` / `foo.scss`). Every other kind does: `-store`,
`-client`, `-service`, `.guard`, `.interceptor`, `.resolver`, `.routes`, `-route`, `.token`,
`.model`. Pure functions under `shared/utils` are the exception. The schematics in `angular.json`
produce the dot forms. A service has no separator option, so name it with the suffix:
`ng g s services/<domain>/<name>-service`.

## Vendored contract

[`src/app/shared/models/andes/assistant-ui.contract.ts`](src/app/shared/models/andes/assistant-ui.contract.ts)
is copied **byte for byte** from the `Andes.Extensions.AI.UI` NuGet package — the same package the API
serialises the chat stream with. It is the one file in this repository that must never be edited by
hand. Its header carries the package id, version and source path, followed by an
`// --- END VENDOR HEADER` sentinel; everything below that line is the package's own bytes.

`npm run check:contract` strips the header, compares the rest against the copy in the local NuGet
cache, and additionally cross-checks the pinned version against every `PackageReference` under
`../enterprise-gpt-api`. That second check is the one that matters in practice: a byte comparison
against the _old_ package folder still passes after the API upgrades, so without it the client would
keep folding a stale contract. The check also scans `src/` for hand-written duplicates of the four
public symbols, so there is exactly one definition of `AssistantUiEvent`, `AssistantActivity`,
`AssistantStatusSnapshot` and `foldAssistantEvents`.

The file is excluded from **both** Prettier (`.prettierignore`) and ESLint (`globalIgnores`). The
package uses double quotes and has two lines past this project's 100-column limit, so `npm run format`
would rewrite it and cause the very drift the check exists to catch.

To upgrade: bump the `PackageReference` in `enterprise-gpt-api`, `dotnet restore`, copy the new package
file over everything below the header, update `@andes-contract-version`, and re-run the check.

`.gitattributes` pins this project's working tree to `eol=lf`. Git for Windows sets
`core.autocrlf=true` in its _system_ config, which checks every file out as CRLF while the index holds
LF — that made `npm run format:check` fail on files nobody had touched, and made a byte-exact
comparison against an LF-only package file impossible.

## Generated assets

| Path                  | Generated by             | In git? |
| --------------------- | ------------------------ | ------- |
| `public/fonts`        | `npm run assets:fonts`   | No      |
| `public/icons`        | `npm run assets:icons`   | No      |
| `public/vendor/katex` | `npm run assets:katex`   | No      |
| `public/brand`        | committed, not generated | **Yes** |

`public/fonts` and `public/icons` are gitignored and rebuilt by `prestart`, `prebuild` and `pretest`,
so a fresh clone never has to think about them. The fonts are the twelve `woff2` faces this app uses,
copied out of `@fontsource`; the icons are one SVG sprite built from the checked-in manifest at
`src/app/shared/utils/icons/icon-names.ts`. Adding an icon means editing that manifest **and** re-running
`npm run assets:icons` — the name is otherwise typed but absent from the sprite, and renders blank.

`public/brand` is different on purpose: its output is **committed**, about 55 kB of images that
change approximately never.

Nothing here is fetched from a third party at runtime. Fonts, icons, Bootstrap and its icon set all
come from npm and ship from this app's own origin; `npm run check:forbidden` fails the build if a
Google Fonts or CDN origin appears anywhere under `src/`.

## UI kit gallery

```bash
npm start   # then open http://localhost:4200/ui-kit
```

Every shared component, light and dark side by side. The route is guarded with
`canMatch: [() => isDevMode()]`, so its chunk is never requested in a production build. It exists
because the kit has no real consumer yet and two of its properties cannot be checked by a unit test:
that a component reads correctly in dark as well as light, and that motion stops under
`prefers-reduced-motion`. See [`docs/frontend/design-system.md`](../docs/frontend/design-system.md).

## Bundle budget

`angular.json` warns above **680 kB** of initial raw bundle and fails above **720 kB**, with a
separate `styles` budget at **66 kB warn / 80 kB error**. MSAL is roughly 249 kB of that and none of
it is deferrable, because `main.ts` must `initialize()` the instance before `bootstrapApplication`.

Re-baseline only when a dependency genuinely has to be initial — check first whether it does, and
record the new figure in the pull request. The budget's real job is to fail when a diagram or math
library leaks out of its lazy chunk, and `scripts/check-initial-chunk.mjs` covers the case a size
budget cannot see: a chunk created by `import('mermaid')` has no entry point for a budget to
attribute bytes to. See [`docs/architecture/frontend.md`](../docs/architecture/frontend.md).
