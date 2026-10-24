# Frontend Structure

The Angular 21 client at `enterprise-gpt-ui/`: standalone components, zoneless change detection,
NgRx Signals for state, Bootstrap 5.3 with SCSS for styling.

## Layers

`src/app/` is feature-first, the Andes `angular-ui-architecture` layout: a folder is named for what
it is about, never for the kind of code in it. A feature such as `chat` keeps its route target,
components, stores and pure functions together in `features/chat/`. Dependencies run one way, and
ESLint enforces it:

```
features -> core | shared        core -> shared        shared -> shared (+ *.model.ts)
```

Features never import each other, `core` never imports a feature, and a `*.model.ts` imports only
other models.

| Folder | Holds | May not import |
| --- | --- | --- |
| `shared/<theme>/` | Presentational components, directives and pure functions grouped by purpose: `dialog/`, `menu/`, `data-table/`, `icons/`, `formatting/`, `layout/` and so on. Kit primitives stay here even when only one feature renders them. | `core` and `features`, except `*.model.ts` |
| `core/<topic>/` | App-wide singletons and policy (`auth`, `api`, `config`, `errors`, `events`, `navigation`, `session`, ...), the root stores more than one feature injects, and the stateful widgets several features render. | `features` |
| `features/<feature>/` | Everything one feature owns, flat: route targets, `*.routes.ts` tables, its guard and `-route.ts` URL contract, components, page- or route-provided stores, and the root stores only it uses. Sub-areas get a folder as they grow (`chat/{markdown,transcript,stream,email}`, `admin/{users,chat-models,mcps,reports}`, `shell/{sidebar,conversation-actions,project-actions}`). | any other feature |
| `src/testing/` | Test-only providers and fixtures. | n/a, specs only |

Three `core/` topics need a note, because they hold more than singletons:

- `core/api/` holds the wire DTOs (`*.model.ts`), the vendored Andes streaming contract
  (`assistant-ui-contract.ts`), `ApiUrl`, `retryInterceptor` and the request-state store features.
- `core/composer/` and `core/uploads/` hold the stateful widgets that the chat and project screens
  both render, each with its own instance: the composer, its menus, `COMPOSER_HOST` and the
  turn-settings and pending-handoff stores; `UploadStore`, `COMPOSER_UPLOADS` and the upload clients.
- A component that injects a store or non-model core code lives in the topic it serves:
  `error-panel` in `errors/`, `project-picker` in `projects/`, the toast region in `notifications/`,
  the theme toggle in `theme/`.

Three components that the dev-only `/ui-kit` gallery shows from `features/` (`badges/permission-badge`,
`badges/kind-badge`, `cards/project-card`) live in `shared/`, so that route obeys the feature ban.

The SSE codec and turn reducers in `features/chat/stream/` are held framework-free by an
`import-x/no-restricted-paths` zone, so they test in Node with no `TestBed`. Other pure functions
stay Angular-free by convention, since no folder marks them.

`eslint-layer-bans.mjs` generates the bans. `layerBans()` emits one `no-restricted-imports` block each
for `shared`, `core` and every folder under `features/`, each with a `*.model.ts` twin.
`layerZones()` restates the feature bans as `import-x/no-restricted-paths` zones, because
`no-restricted-imports` never sees a dynamic `import()`. `eslint-plugin-import-x` also runs `no-cycle`
and `no-relative-parent-imports`, and `banForms` rejects `FormsModule` and `ReactiveFormsModule`
everywhere. Specs are exempt from all of these except the forms ban. Prettier sorts imports Angular,
third-party, `@core`, `@shared`, `@features`, `@testing`, then relative.

Path aliases are `@core/*`, `@features/*`, `@shared/*` and `@testing/*`. An import that crosses a
top-level folder or reaches a parent folder uses the alias; same-folder and child imports stay
relative (`./`). `src/testing/` imports `../app/...` instead, because `setup-dom.ts` and
`media-query.ts` load as a Vite setup file outside the alias map and the other helpers follow suit.

File names are the Angular CLI defaults, and `angular.json` pins no naming option. Components,
directives and services carry no suffix (`auth.ts` holds `Auth`, `theme.ts` holds `Theme`). Guards,
interceptors and tokens take a dash suffix (`auth-guard.ts`, `retry-interceptor.ts`,
`app-config-token.ts`). Stores are `-store`, store features `with-*`, transports `-client`, URL
contracts `-route`. A dot appears only in `.spec.ts`, `.a11y.spec.ts`, `.routes.ts`, `.config.ts` and
`.model.ts`. There are no barrel `index.ts` files and no re-exports.

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
| `provideAppInitializer(() => inject(Auth))` | Eager and load-bearing — its constructor subscribes this tab to another tab's sign-out. |
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
It also fails if any `features/` file other than the eager auth pages reaches the initial graph, which
keeps the signed-in screens in lazy chunks. So do the pieces outside `features/` that only lazy
features render: `core/composer/`, `core/uploads/`, `ProjectConversationsStore`
(`core/projects/project-conversations-store.ts`), and the shared `permission-badge`, `kind-badge` and
`project-card`.

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
