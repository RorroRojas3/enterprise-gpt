# ai-chat-ui — Angular Frontend Guide

This file is the per-folder Claude instructions for `ai-chat-ui/`. It is auto-loaded when working in this directory. The rules here are non-negotiable and reflect both official Angular v21 best practices (verified via `mcp__angular-cli__get_best_practices`) and the conventions already used in this codebase.

When you need deeper Angular guidance during a task, call:
- `mcp__angular-cli__get_best_practices` — version-specific official guide
- `mcp__angular-cli__search_documentation` — searches angular.dev for the project's Angular major version
- `mcp__angular-cli__list_projects` — workspace metadata

---

## Project Overview

- **Angular 21** SPA, **standalone components**, **signals-first**
- **Build system:** `@angular/build:application` (Angular 21+ application builder) — do not change
- **Component prefix:** `app`
- **Style language:** SCSS (`inlineStyleLanguage: "scss"` in `angular.json`)
- **File-naming style guide:** **`2016`** (`feature.component.ts` / `feature.component.html` / `feature.component.scss`). Angular 21 also supports a `2025` style (`feature.ts`/`feature.html`/`feature.scss`) — **do not mix styles**. All new files must use `2016`.
- **Change detection:** zone.js (via `provideZoneChangeDetection({ eventCoalescing: true })`). The app is **not zoneless** — do not introduce zoneless code paths.
- **Auth:** Azure AD via `@azure/msal-angular` v4 + `@azure/msal-browser` v4
- **State:** `@ngrx/signals` `signalStore()` instances under `src/app/store/` plus a central `StoreService` (`src/app/services/store.service.ts`)
- **UI:** Bootstrap 5.3.x + `bootstrap-icons`. Bootstrap utilities first, custom SCSS only when utilities don't suffice.
- **Markdown:** `markdown-it` + `markdown-it-highlightjs` + `highlight.js`
- **Streaming:** native Fetch API (Server-Sent Events) — `HttpClient` cannot stream SSE
- **API base URL:** `environment.apiUrl`

## Project Structure

```
src/app/
├── components/      reusable feature components
├── pages/           page-level (route target) components
├── services/        HTTP/data services and the central StoreService
├── store/           @ngrx/signals signalStore() instances
├── dtos/            request/response models (mirror backend RR.AI-Chat.Dto)
├── interceptors/    functional HTTP interceptors
├── shared/          shared UI: navbar, menu-offcanvas, notification
├── app.component.* root component
├── app.config.ts    bootstrapApplication config (providers, interceptors, MSAL)
├── app.routes.ts    Routes[] with MsalGuard
└── main.ts          bootstrapApplication entry
```

Place new code accordingly: a new feature page goes under `pages/`, a new HTTP service under `services/`, a new bounded feature store under `store/`, and request/response shapes under `dtos/`. Keep DTO field names in sync with the backend `RR.AI-Chat/RR.AI-Chat.Dto/*`.

## TypeScript Standards

- `strict: true` is required, plus `strictTemplates`, `strictInjectionParameters`, `strictInputAccessModifiers`, `noImplicitOverride`, `noPropertyAccessFromIndexSignature`, `noImplicitReturns`, `noFallthroughCasesInSwitch`. Target ES2022. **Never weaken these flags.**
- Prefer type inference when the type is obvious.
- No `any` — use `unknown` if the type is uncertain, then narrow with type guards.
- Define DTOs in `dtos/` and use them as the contract for HTTP responses.

## Components

- **Standalone only.** Never write `NgModule`s.
- **Do not** set `standalone: true` in the `@Component` decorator — it's the default in Angular v20+.
- **Signals API everywhere:**
  - Inputs: `input()` / `input.required<T>()` — never `@Input()`
  - Outputs: `output<T>()` — never `@Output()`
  - Queries: `viewChild()`, `viewChildren()`, `contentChild()`, `contentChildren()` — never decorators
  - State: `signal()`, `computed()`, `effect()` — never `mutate`; use `update`/`set`
  - **`linkedSignal()`** (Angular 21) — for derived state that the user can override/reset. Prefer over a `signal` + `effect` pair when you need both reactivity and writability.
- `changeDetection: ChangeDetectionStrategy.OnPush` on every component.
- Host bindings via the `host: { ... }` object in the decorator — never `@HostBinding` / `@HostListener`.
- Class/style: use `[class.x]` / `[style.x]` bindings — never `ngClass` / `ngStyle`.
- Use `NgOptimizedImage` for static images. (It does not work for inline base64.)
- External templates and SCSS files match the project pattern; inline only for trivially small components.
- When using external templates/styles, paths are relative to the component TS file.

## Templates

- **Keep templates simple — push complex logic into the component class or a service.**
- **Native control flow only:** `@if`, `@for` (with `track`), `@switch`. Never `*ngIf` / `*ngFor` / `*ngSwitch`.
- No arrow functions in templates (unsupported).
- No globals in templates (e.g. don't reference `new Date()` directly — derive from a signal/computed).
- Use the `async` pipe for any remaining observables not already wrapped in a signal.
- Reactive forms (`FormGroup`, `FormControl`) only — never template-driven.

## Services

- `@Injectable({ providedIn: 'root' })` for singletons.
- `inject()` for dependencies — never constructor injection. Every service in this project already follows this.
- Single responsibility per service.
- Return `Observable<T>` from HTTP services with proper typing.

## State Management

This codebase uses **two coexisting patterns** — match what's already there:

1. **`StoreService`** (`services/store.service.ts`) — a central reactive service that owns active-conversation state: `conversation`, `messages`, `isStreaming`, `stream`, `fileExtensions`, `projectId`, pagination. Exposes signals + setter methods. Use this for cross-component conversation state.
2. **`@ngrx/signals` `signalStore()`** — feature stores in `store/` (`model.store.ts`, `mcp.store.ts`, `user.store.ts`). Bounded feature state. Compose in this order: `withState` → `withComputed` (if needed) → `withMethods`:

   ```ts
   export const FooStore = signalStore(
     { providedIn: 'root' },
     withState(initialState),
     withComputed((store) => ({
       /* derived selectors using computed(() => ...) */
     })),
     withMethods((store) => ({
       /* mutators / query helpers — plain synchronous functions */
     })),
   );
   ```

   - State updates use `patchState(store, { ... })` inside `withMethods`.
   - Derived state goes in `withComputed` using `computed(() => ...)`. Templates bind directly: `@if (userStore.isAdmin()) { ... }`. Don't compute the same derivation in templates.
   - Methods are **plain synchronous functions** — do not use `rxMethod` from `@ngrx/signals/rxjs-interop`. The project keeps RxJS subscriptions in components/services; stores own only state.
   - Read signals inside methods/computeds by calling them: `store.user()`.
   - Every public method gets a JSDoc summary.
   - Never expose mutable state — consumers read signals or computed selectors only.

Rules:
- Store API response data in signals so the UI is reactive.
- Keep state transformations **pure and predictable** — no side effects inside `computed()` or store updaters; isolate side effects in services or `effect()`.
- Never expose mutable state directly — return readonly signals or `computed()` derivatives.
- Loading and error state lives in signals next to the data they describe.
- **Do not introduce a new state-management library.** Extend `StoreService` or add a new `signalStore()`.

## HTTP & API Layer

- HTTP is configured in `app.config.ts` via `provideHttpClient(withInterceptors([httpInterceptor]), withInterceptorsFromDi(), withFetch())`. **Both** functional and DI-based interceptor pipelines are wired (functional for our own, DI-based because `@azure/msal-angular` ships a class-based `MsalInterceptor`).
- The functional **`httpInterceptor`** (`interceptors/http.interceptor.ts`) handles **global error handling**: parses `ErrorDto { errors[], traceId, timestamp }`, maps status codes (`0`, 400, 401, 403, 404, 408, 422, 429, 500, 502, 503, 504) to user-facing notifications via `NotificationService`, and rethrows the error so callers can still react. **Do not duplicate this logic per-service.** If a new status code needs handling, add it to `displayErrorByStatus`.
- The class-based **`MsalInterceptor`** auto-attaches Bearer tokens for URLs in `MSALInterceptorConfigFactory`'s `protectedResourceMap`.
- For **SSE streaming** endpoints, follow `ConversationService.getServerSentEvent()`: use the native Fetch API with a manually acquired MSAL token. `HttpClient` cannot stream SSE.
- RxJS error handling in services with `catchError`. Use `takeUntilDestroyed()` from `@angular/core/rxjs-interop` (in field initializers) for subscription cleanup. Implement `OnDestroy` only when `takeUntilDestroyed()` can't handle the cleanup (e.g., third-party library handles).
- Cache shared observables with `shareReplay(1)`.

### `httpResource` (Angular 21, experimental — for GETs)

Angular 21 ships `httpResource`, a reactive `HttpClient` wrapper that returns signals: `value()`, `isLoading()`, `error()`, `hasValue()`. It is built **on top of** `HttpClient`, so the existing `httpInterceptor` and `MsalInterceptor` apply automatically.

For new **read** endpoints, prefer `httpResource(() => \`${environment.apiUrl}/x/${id()}\`)` over `HttpClient + Observable + manual signal()`. Reactive params (signals in the URL/request) trigger automatic refetch and outstanding-request cancellation.

**Do not** use `httpResource` for mutations (POST/PUT/DELETE) — keep those on `HttpClient`. SSE streaming continues to use Fetch.

## Routing

- Routes live in `app.routes.ts` as a single `Routes[]`.
- All authenticated routes use `canActivate: [MsalGuard]` — keep new authenticated routes on the same guard.
- Current routes use eager `component:` references. For **new** non-critical feature pages, prefer `loadComponent: () => import('./pages/foo/foo.component').then(m => m.FooComponent)` to keep the initial bundle small.
- Read route params via `inject(ActivatedRoute)` and convert to signals with `toSignal(...)` where useful.

## Styling

- SCSS, component-scoped (`ViewEncapsulation.Emulated` default — don't change).
- **Bootstrap 5 utility classes first**; write custom SCSS only when utilities don't suffice.
- Bootstrap Icons via `bi bi-*` classes.
- Responsive: Bootstrap grid + Flexbox utilities.
- Global styles only in `src/styles.scss`.

## Accessibility

- Must pass AXE checks and meet WCAG AA minimums (focus management, color contrast, ARIA).
- Semantic HTML first; ARIA attributes only where semantics aren't enough.
- Keyboard navigability for every interactive element.
- Visible focus states — do not suppress the browser outline without an equivalent replacement.
- Text contrast ≥ 4.5:1.

## Security

- Sanitize untrusted HTML via Angular's `DomSanitizer`. The markdown render path already produces `SafeHtml`.
- **Never** bypass sanitization (`bypassSecurityTrustHtml`, etc.) on user-controlled input.
- No direct DOM manipulation — use Angular bindings or `Renderer2`.
- Validate forms with reactive form validators and custom validators where needed.
- Never log MSAL tokens.

## Lifecycle & Memory

- Use `takeUntilDestroyed()` in field initializers — already the project pattern.
- Implement `OnDestroy` only when there's cleanup `takeUntilDestroyed()` can't cover (e.g., a third-party library handle to release).

## Pre-flight Checklist (do this before writing code)

1. Confirm Angular version in `package.json` is still `21.x` — if it has moved, re-call `mcp__angular-cli__get_best_practices` and update this file.
2. Confirm `angular.json` `builder` is still `@angular/build:application`.
3. Confirm `app.config.ts` still calls `provideZoneChangeDetection(...)` — i.e. the app is not zoneless.
4. When generating new files, use the CLI with the correct flags:
   ```
   ng generate component <path> --change-detection=OnPush --file-name-style-guide=2016
   ```
5. Build (`ng build`) or serve (`ng serve`) before assuming a syntax issue is real.
6. Don't change `angular.json` build configuration unless explicitly asked.

## Do / Don't Quick Reference

**Do**
- Use `input()` / `output()` / `signal()` / `computed()` / `linkedSignal()`
- Use `inject()` and `providedIn: 'root'`
- Use `@if` / `@for` (with `track`) / `@switch`
- Use the `host: { ... }` object for host bindings
- Use `[class.x]` / `[style.x]` bindings
- Use `NgOptimizedImage` for static images
- Use `httpResource` for new GET endpoints; keep mutations on `HttpClient`
- Use `takeUntilDestroyed()` for subscription cleanup
- Use `--file-name-style-guide=2016` when generating files

**Don't**
- Don't write `@Input()` / `@Output()` / `@HostBinding` / `@HostListener`
- Don't use `ngClass` / `ngStyle` / `*ngIf` / `*ngFor` / `*ngSwitch`
- Don't add `standalone: true` (it's the default in v20+)
- Don't use `mutate` on signals — use `update` / `set`
- Don't write arrow functions or global references in templates
- Don't introduce a new state-management library
- Don't bypass `DomSanitizer` on user-controlled input
- Don't change `angular.json` build configuration
- Don't duplicate global error handling per-service — extend `httpInterceptor`

## Known migration debt

A handful of older files still use legacy patterns. **Do not copy them when writing new code** — the rules above (signals API, no `ngClass`, etc.) win. Migrate opportunistically when you touch one of these files:

- `src/app/shared/navbar/navbar.component.ts` — uses `@Output() ... = new EventEmitter<void>()`. Should be `output<void>()`.
- `src/app/shared/menu-offcanvas/menu-offcanvas.component.ts` — uses `@Input()` / `@Output()`. Should be `input()` / `output()`.
- `src/app/shared/components/notification/notification.component.html` — uses `[ngClass]`. Should be `[class.x]` bindings.

> **Testing:** the repo has Jasmine/Karma configured but no `.spec.ts` files. When test coverage begins, add a Testing section here covering `TestBed`, `provideHttpClientTesting`, `jasmine.createSpyObj`, and signal-based testing.
