# Frontend Structure

The Angular 21 client at `enterprise-gpt-ui/`: standalone components, zoneless change detection,
NgRx Signals for state, Bootstrap 5.3 with SCSS for styling.

## Layers

`src/app/` is grouped by *kind* first and by *feature* second, so a feature such as `chat` has
`pages/chat/`, `components/chat/` and `state/chat/`. Dependencies run one way, and ESLint enforces
it:

```
pages -> components/<feature> -> shared/{components,directives,pipes} -> state -> services -> core -> shared/utils -> shared/models
```

Importing across features (`components/a` to `components/b`, `pages/a` to `pages/b`) is banned too.

| Layer | Holds | May not import |
| --- | --- | --- |
| `shared/models/` | Wire DTO types (`api/*.model.ts`) and the vendored streaming contract (`andes/`). Zero Angular or NgRx imports. | everything else in `app/` |
| `shared/utils/<topic>/` | Framework-free pure functions: the SSE codec, turn reducers, formatters, and the `-route.ts` contracts a store reads. Zero Angular or NgRx imports, so it tests in Node with no `TestBed`. | everything but `shared/models` |
| `core/` | App-wide singletons and policy: auth, config, errors, events, HTTP, navigation, the signal-store features, cross-feature tokens, and the root stores that guards, interceptors and core services inject. | `services`, `state`, `shared/{components,directives,pipes}`, `components`, `pages` |
| `services/<domain>/` | Transports: the conversation stream client and the upload clients, each with its test-seam token beside it. | `state`, `shared/{components,directives,pipes}`, `components`, `pages` |
| `state/` | Root stores flat; stores provided on a page or route in `state/<feature>/`. | `shared/{components,directives,pipes}`, `components`, `pages` |
| `shared/{components,directives,pipes}/` | Presentational, reusable, feature-agnostic building blocks. May inject root stores from `state/`. | `state/<feature>/`, `components`, `pages` |
| `components/<feature>/` | What one feature renders. `components/composer` is a pageless feature that the chat and project pages both compose. | `pages`, and any other feature |
| `pages/<feature>/` | Route targets, `*.routes.ts` tables, guards and resolvers. Only the auth pages are eager. | other features |

The table reads innermost first, so each layer imports only from the rows above it.

A root store stays in `core/` when a guard, an interceptor or a core service injects it, because
`core` may not import `state`. Those are `SessionStore`, `ModelCatalogStore`, `McpCatalogStore`,
`ConversationListStore`, `ProjectLookupStore` and `ToastStore`. Every other root store lives in
`state/`.

`eslint-layer-bans.mjs` generates the bans. `layerBans()` emits one `no-restricted-imports` block per
layer and per feature folder. `layerZones()` restates the app-path bans as
`import-x/no-restricted-paths` zones, because `no-restricted-imports` never sees a dynamic
`import()`. `eslint-plugin-import-x` also runs `no-cycle` and `no-relative-parent-imports`, and
`banForms` rejects `FormsModule` and `ReactiveFormsModule` everywhere. Specs are exempt from all of
these except the forms ban. Prettier sorts imports bottom-up through the same layers.

Path aliases are `@pages/*`, `@components/*`, `@state/*`, `@services/*`, `@core/*`, `@shared/*` and
`@testing/*`. Imports cross folders through the alias, never `../`. Components follow the v21
convention — `foo.ts` / `foo.html` / `foo.scss`, no `.component` suffix — while every other kind
carries one (`-store`, `-client`, `-service`, `.guard`, `.interceptor`, `.resolver`, `.routes`,
`-route`, `.token`, `.model`). There are no barrel `index.ts` files.

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
2. `_bootstrap-config.scss` — the palette and breakpoints fed into Bootstrap's Sass variables, the full
   utilities API plus the app's extensions, and `root`
3. `_theme.scss` — emits the token maps from `_tokens.scss` per theme, overriding the `--bs-*` values
   `root` just emitted
4. `_bootstrap-components.scss` — every Bootstrap module
5. `_base.scss`, `_focus.scss` (after Bootstrap's components so it beats their focus styles)
6. `_overlays.scss` — the native `<dialog>` bridge for Bootstrap's modal and offcanvas
7. `_bootstrap-overrides.scss` — theme colours that must flip, and the named focus rules
8. `_kit.scss` — classes Bootstrap lacks, after the overrides so a modifier wins
9. `_prism.scss`, `_markdown.scss`
10. `bootstrap/scss/utilities/api` — second to last, so a utility beats a component rule
11. `_motion.scss` — **last**, so its reduced-motion block suppresses everything above

`_tokens.scss` is data only (the `$tokens-light` / `$tokens-dark` maps); `_theme.scss` is what emits
them. Components read tokens through `var(--...)` and must **never** `@use 'tokens'` or `@use 'theme'`,
which emit `:root` and would re-scope it to the component; `@use 'breakpoints' as bp` is fine.
Templates use Bootstrap and kit classes, and a component stylesheet keeps only bespoke rules. There is
no Bootstrap JavaScript. See [../frontend/design-system.md](../frontend/design-system.md).

## Build gates

`npm run build` runs `ng build` and then `scripts/check-initial-chunk.mjs` over the esbuild
metafile. `npm run lint` runs ESLint plus three Node checks. See
[../development/testing-and-ci.md](../development/testing-and-ci.md) for what each asserts.

The production build **warns above 900 kB** initial raw and **fails above 960 kB**, with a `styles`
budget at 264/282 kB (full Bootstrap). MSAL is roughly 249 kB of the initial graph and none of it is deferrable, since
`main.ts` must `initialize()` the instance before `bootstrapApplication`.

The rule to preserve is which libraries stay off the initial graph: the markdown stack
(`ngx-markdown`, `marked`, `prismjs`, `dompurify`) rides the lazy chat chunk; `mermaid`, `katex` and
`@microsoft/applicationinsights-web` are reached only through `await import(...)` behind DI tokens
and `config.json` flags, and sit in lazy chunks of their own. `check-initial-chunk.mjs` fails the
build if a static import from `main.ts` reaches any of them, and names the import path that did it.
It also fails if any `pages/`, `components/` or `state/<feature>/` file other than the eager auth
pages reaches the initial graph, which keeps the signed-in screens in lazy chunks.

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
| `enterprise-gpt-ui/eslint.config.mjs` | The lint rule set and the forbidden-API rules |
| `enterprise-gpt-ui/eslint-layer-bans.mjs` | Generates the layer bans and import-path zones |
| `enterprise-gpt-ui/scripts/check-initial-chunk.mjs` | The must-stay-lazy gate |
| `enterprise-gpt-ui/src/app/core/config/load-app-config.spec.ts` | Config validation tests |

## Related

- [overview.md](overview.md)
- [../frontend/state.md](../frontend/state.md)
- [../frontend/errors.md](../frontend/errors.md)
