# Frontend Structure

The Angular 21 client at `enterprise-gpt-ui/`: standalone components, zoneless change detection,
NgRx Signals for state, Bootstrap 5.3 with SCSS for styling.

## Layers

Dependencies run one way, and ESLint enforces it with `no-restricted-imports`:

```
features -> shared -> core -> domain
```

| Layer | Holds | May not import |
| --- | --- | --- |
| `domain/` | Framework-free logic: wire DTO types, the SSE codec, turn reducers, formatters. Zero Angular imports, so it tests in Node with no `TestBed`. | `core`, `shared`, `features` |
| `core/` | App-wide singletons and pure policy: auth, config, HTTP, errors, events, root stores. | `shared`, `features` |
| `shared/` | Presentational, reusable, feature-agnostic components and directives. | `features` |
| `features/` | Route-owned screens with their own stores. | — |

Path aliases are `@core/*`, `@domain/*`, `@shared/*`, `@features/*`, `@testing/*`. Components follow
the v21 convention — `foo.ts` / `foo.html` / `foo.scss`, no `.component` suffix. There are no barrel
`index.ts` files.

## Bootstrap

`src/main.ts` runs before the injector exists, in this order:

1. `Promise.all([loadAppConfig(), loadIconSprite()])`. `config.json` is fetched with
   `cache: 'no-store'`, resolved against `document.baseURI`, and validated by a narrow assertion.
   The icon sprite is overlapped so there is no icon pop-in on first paint; it never rejects.
   A missing or invalid config renders the static fatal shell in `index.html` and **does not
   bootstrap**.
2. `createStandardPublicClientApplication(...)` — MSAL is created *and* initialized here, because
   `initialize()` is async and no synchronous DI factory can await it. A malformed authority or
   client id fails here rather than as an opaque Entra error at sign-in.
3. `bootstrapApplication(App, ...)` with `APP_CONFIG` and `MSAL_INSTANCE` provided as real values.

There is no `environments/` directory and no `fileReplacements`. `public/config.json` is the
committed development copy and is replaced per environment.

The startup shell is not removed in `main.ts`; `provideStartupShellHandoff()` waits for the first
routed component to render, otherwise an empty app shell paints over the startup card.

### Providers worth knowing

| Provider | Why it is written the way it is |
| --- | --- |
| `provideRouter(..., withEnabledBlockingInitialNavigation())` | Without it the app shell paints behind the startup card while `authGuard` is still deciding. |
| `provideHttpClient(withFetch(), withInterceptors([retryInterceptor, authInterceptor]))` | Order is load-bearing: retry is outermost, so each retried attempt re-enters auth and carries a fresh token. `withFetch` makes `HttpClient` and the raw-fetch stream transport share network semantics. |
| `provideAppInitializer(() => inject(AuthService))` | Eager and load-bearing — its constructor subscribes this tab to another tab's sign-out. |
| `providePreventDropNavigation()` | A stray file drop would otherwise navigate away mid-stream. |

## Routing

`src/app/app.routes.ts` is eager; almost everything under it is lazy.

| Path | Loads | Guard |
| --- | --- | --- |
| `auth`, `login-failed`, `session-error`, `signed-out` | Eager auth screens | none — `auth` *is* the redirect URI |
| `ui-kit` | Lazy | `canMatch: isDevMode()` |
| `''` | children | `canActivate: [authGuard]` |
| `''` (nested) | children | `canActivate: [sessionGuard]` |
| `forbidden` | Eager, sibling of the shell so it renders without chrome | inherits |
| `''` | **Lazy** `Shell` | inherits |
| `chat`, `chat/{id}` | Lazy `Chat`, matched by `chatMatcher` | inherits |
| `conversations`, `projects`, `documents` | Lazy | inherits |
| `admin` | Lazy | `canMatch: [adminCanMatch]` |

Two structural rules:

- **Guards in one array run concurrently.** `authGuard` and `sessionGuard` are therefore *nested*,
  one per level — nesting is the only way to order them. `sessionGuard` issues `POST api/users/me`
  and must not run before a token exists.
- **`canMatch` runs during URL recognition, `canActivate` after it.** Only `canMatch` keeps a lazy
  chunk off the wire, which is why `/admin` uses it.

`chatMatcher` matches both `/chat` and `/chat/{id}` in one route config, so the component is reused
rather than remounted when a conversation is created around the first prompt.

The shell and every route under it are lazy, which is what keeps the signed-in chrome off the four
unguarded auth routes.

## Styling

`src/styles.scss` is an `@import` chain, not `@use`, because Bootstrap 5.3 still shares one global
`!default` namespace across its partials. Statement order is output order and is load-bearing:

1. `_typography.scss` — self-hosted `@font-face` and the font stacks
2. `_bootstrap-config.scss` — Bootstrap configuration, utilities trim, and `root`
3. `_tokens.scss` — the design tokens, overriding the `--bs-*` values `root` just emitted
4. `_bootstrap-components.scss`
5. `_base.scss`, `_focus.scss` (after Bootstrap's components so it beats their focus styles), `_kit.scss`
6. `_bootstrap-overrides.scss`, `_prism.scss`, `_markdown.scss`
7. `bootstrap/scss/utilities/api` — second to last, so a utility beats a component rule
8. `_motion.scss` — **last**, so its reduced-motion block suppresses everything above

Components read tokens through `var(--...)` and must **never** `@use 'tokens'`, which emits `:root`
and would re-scope it to the component. See [../frontend/design-system.md](../frontend/design-system.md).

## Build gates

`npm run build` runs `ng build` and then `scripts/check-initial-chunk.mjs` over the esbuild
metafile. `npm run lint` runs ESLint plus three Node checks. See
[../development/testing-and-ci.md](../development/testing-and-ci.md) for what each asserts.

The production build **warns above 680 kB** initial raw and **fails above 720 kB**, with a `styles`
budget at 66/80 kB. MSAL is roughly 249 kB of the initial graph and none of it is deferrable, since
`main.ts` must `initialize()` the instance before `bootstrapApplication`.

The rule to preserve is which libraries stay off the initial graph: the markdown stack
(`ngx-markdown`, `marked`, `prismjs`, `dompurify`) rides the lazy chat chunk; `mermaid`, `katex` and
`@microsoft/applicationinsights-web` are reached only through `await import(...)` behind DI tokens
and `config.json` flags, and sit in lazy chunks of their own. `check-initial-chunk.mjs` fails the
build if a static import from `main.ts` reaches any of them, and names the import path that did it.

Re-baseline the budget only when a dependency genuinely has to be initial — check first whether it
does.

## TypeScript

`tsconfig.json` is strict, and additionally sets `strictTemplates`,
`noPropertyAccessFromIndexSignature`, `noUncheckedIndexedAccess`, `noUnusedLocals` and
`noUnusedParameters`. Unused-symbol detection is tsc's rather than ESLint's, because tsc counts a
`{@link}` as a use. `baseUrl` is deliberately unset, so the `paths` entries use relative forms.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/main.ts` | Pre-injector bootstrap sequence |
| `enterprise-gpt-ui/src/app/app.config.ts` | The provider list |
| `enterprise-gpt-ui/src/app/app.routes.ts` | Route table and guard nesting |
| `enterprise-gpt-ui/src/app/core/config/load-app-config.ts` | Runtime config fetch and validation |
| `enterprise-gpt-ui/src/styles.scss` | The ordered import chain |
| `enterprise-gpt-ui/eslint.config.mjs` | Layer bans and the forbidden-API rules |
| `enterprise-gpt-ui/scripts/check-initial-chunk.mjs` | The must-stay-lazy gate |
| `enterprise-gpt-ui/src/app/core/config/load-app-config.spec.ts` | Config validation tests |

## Related

- [overview.md](overview.md)
- [../frontend/state.md](../frontend/state.md)
- [../frontend/errors.md](../frontend/errors.md)
