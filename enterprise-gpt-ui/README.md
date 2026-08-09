# Enterprise GPT — web client

Angular 21 client for the Enterprise GPT API. Rebuilt from scratch against
[`docs/prd/enterprise-ui-rebuild.md`](../docs/prd/enterprise-ui-rebuild.md); no code was migrated
from the previous `enterprise-ui/` app.

Zoneless change detection, standalone components, `@ngrx/signals` for state, Bootstrap 5.3 + SCSS,
MSAL for Entra ID, and Vitest via `@angular/build:unit-test`.

## Prerequisites

- Node 22+ and npm 11+
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
npm run build      # production build into dist/
npm test           # Vitest, single run
npm run test:watch # Vitest, watch mode
npm run format     # Prettier
```

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

Dependencies run one way only: **`features` → `shared` → `core` → `domain`**. Nothing imports
upward, and `domain/` imports nothing from Angular so the stream codec and the activity fold stay
testable in Node with no `TestBed`.

| Path                | Holds                                                                                                                                                                              |
| ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/app/core/`     | App-wide singletons and pure policy: runtime config, error normalization, HTTP, auth, session, theme, cross-store events, and the composable signal-store features. No components. |
| `src/app/domain/`   | Framework-free types and pure functions: DTO mirrors, the vendored streaming contract, the SSE codec, the timeline index.                                                          |
| `src/app/shared/`   | Presentational building blocks. No store, no HTTP.                                                                                                                                 |
| `src/app/features/` | Route-owned screens, each owning its own stores. `features/admin` is lazy and guarded with `canMatch`, so its chunk is never fetched by a non-administrator.                       |
| `src/styles/`       | Sass partials, reachable as `@use 'tokens'` from any component (`angular.json` adds this directory to the load path).                                                              |
| `src/testing/`      | Test-only providers and shared fixtures. Excluded from `tsconfig.app.json`.                                                                                                        |

Path aliases `@core/*`, `@domain/*`, `@shared/*`, `@features/*` and `@testing/*` are configured in
`tsconfig.json`. Components follow the v21 convention — `foo.ts` / `foo.html` / `foo.scss`, with no
`.component` suffix.

## Bundle budget

`angular.json` fails the production build above **300 kB** of initial raw bundle and warns above
**240 kB**. The current baseline is **220 kB raw / 60 kB transfer**.

Re-baseline the figure whenever a story adds a substantial dependency to the initial chunk — MSAL,
Bootstrap, markdown-it — and record the new number in the pull request. The budget's real job is to
fail when a diagram or math library leaks out of its lazy chunk, which only works if the ceiling
stays close to the measured size.
