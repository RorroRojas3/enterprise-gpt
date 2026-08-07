---
paths:
  - "**/*.razor"
  - "**/*.razor.cs"
  - "**/*.razor.css"
---

# Standalone Blazor WebAssembly (.NET 10)

These rules target **standalone Blazor WebAssembly** apps (`blazorwasm` template): pure client-side .NET running in the browser, deployed as static files (CDN, static hosting, PWA). Grounded in the official docs at `learn.microsoft.com/aspnet/core/blazor` (`?view=aspnetcore-10.0`).

## Scope and hosting

- The "ASP.NET Core Hosted" WASM template was **removed in .NET 8** — never scaffold or suggest the old Client/Server/Shared three-project layout. A standalone WASM app calls its backend as an ordinary web API.
- Standalone WASM has **no concept of render modes** — `@rendermode`, `InteractiveWebAssembly`, `InteractiveAuto`, prerendering, and `.Client` projects are Blazor Web App concerns. Never suggest them here.
- Everything shipped to the browser is inspectable: **never put secrets, API keys, or private business logic in the app**. Sensitive work belongs behind a server API.
- Apply the general C# standards (`.claude/rules/csharp.md`): C# 14, primary constructors with `private readonly` `_camelCase` field capture, collection expressions, `is null` / `is not null`.

## Components and rendering performance

- Prefer **primitive immutable parameter types** (`string`, `int`, `bool`, `DateTime`) so built-in change detection can skip child subtrees; for complex parameters, override `ShouldRender` with your own change tracking on hot components.
- Use `Virtualize<TItem>` for long lists (`ItemsProvider` for remote data, tune `ItemSize`/`OverscanCount`); `QuickGrid` has virtualization built in.
- Put `@key` **on the repeated element or component itself**, not on a wrapper — and only when preserving instances/element state matters; it has a small cost otherwise.
- Avoid thousands of tiny component instances (~0.06 ms overhead each in WASM): inline repeated children or use reusable `RenderFragment` fields instead of dedicated components.
- Event handlers trigger an automatic `StateHasChanged`; for handlers that don't change state, use the `EventUtil`/`IHandleEvent` pattern to suppress it (dispatch exceptions via `DispatchExceptionAsync` so error boundaries still see them).
- Never compute `RenderTreeBuilder` sequence numbers (`seq++`) — hardcode literals (analyzer ASP0006); prefer `.razor` markup over manual `RenderTreeBuilder` code entirely.
- Use lifecycle methods correctly: `OnInitializedAsync` for one-time init, `OnParametersSetAsync` for parameter-driven work, `OnAfterRender{Async}` for anything touching `ElementReference`.

## State management

- Share state via DI-registered **state container services** (in WASM, scoped ≈ singleton for the app's lifetime) and cascading values for ancestor→descendant flow. Blazor has no opinionated store; keep containers simple and notify with events/`Action` callbacks that call `StateHasChanged`.
- Persist across reloads with `localStorage` (survives restarts, shared across tabs) or `sessionStorage` (per-tab) via JS interop or a package such as Blazored.LocalStorage.
- **`ProtectedLocalStorage`/`ProtectedSessionStorage` are server-side Blazor only** — they rely on server Data Protection and must never be suggested in WASM.
- Client-side storage is user-visible and tamperable — no sensitive data, ever. Durable, cross-device state belongs in server storage behind a web API.

## HTTP and APIs

- `HttpClient` in WASM is implemented on the browser **Fetch API**. Register the base client once: `builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });` for a same-origin API; use named/typed clients (`AddHttpClient`, `Microsoft.Extensions.Http`) for multiple or external APIs.
- Use `System.Net.Http.Json` helpers (`GetFromJsonAsync`, `PostAsJsonAsync`) and wrap calls in `try`/`catch` with user-facing feedback.
- **.NET 10: response streaming is on by default** — `ReadAsStreamAsync` returns a `BrowserHttpReadStream` that throws on synchronous reads. Opt out per request with `SetBrowserResponseStreamingEnabled(false)` or globally with `<WasmEnableStreamingResponse>false</WasmEnableStreamingResponse>`.
- CORS is fixed on the **server/endpoint**, not the client — `NoCors` mode is not a workaround. If you can't configure the external API, relay through your own backend.
- Attach access tokens with `BaseAddressAuthorizationMessageHandler` (same-origin) or a custom `AuthorizationMessageHandler` configured with `authorizedUrls`/`scopes` wired via `.AddHttpMessageHandler<>()`.

## Authentication

- Use `Microsoft.AspNetCore.Components.WebAssembly.Authentication`: `AddOidcAuthentication` for any OIDC provider (config in `wwwroot/appsettings.json`), or `Microsoft.Authentication.WebAssembly.Msal` + `AddMsalAuthentication` for Microsoft Entra ID (`DefaultAccessTokenScopes`, `AdditionalScopesToConsent`).
- Only the **PKCE authorization code flow** is supported — never configure or describe the implicit grant flow.
- Client-side auth checks and validation are UX only — the server API must enforce authorization and re-validate every request.

## JS interop

- Prefer **JS isolation via ES modules**: `_module = await JS.InvokeAsync<IJSObjectReference>("import", "./scripts/foo.js")`; dispose the `IJSObjectReference` in `DisposeAsync`.
- `ElementReference` is only valid from `OnAfterRender{Async}` onward.
- WASM-only optimization: cast to `IJSInProcessRuntime` / `IJSInProcessObjectReference` for synchronous calls when the overhead of async round-trips matters.
- For high-frequency interop, use source-generated `[JSImport]`/`[JSExport]` (`System.Runtime.InteropServices.JavaScript`); `IJSUnmarshalledRuntime` is obsolete. .NET 10 adds `InvokeConstructorAsync`, `GetValueAsync`/`SetValueAsync` for JS objects and properties.
- Crossing the interop boundary per DOM property is slow and churns the GC — batch DOM work inside JS.

## Error handling and logging

- Use **narrowly scoped `ErrorBoundary`** components; subclass and override `OnErrorAsync` to log; call `Recover()` from `OnParametersSet` on navigation for broadly scoped boundaries. Don't leak exception details to users.
- Exceptions from timers/callbacks outside the lifecycle bypass boundaries — dispatch them with `ComponentBase.DispatchExceptionAsync`.
- `ILogger<T>` works but writes to the **browser dev-tools console only**. Configure levels via `builder.Logging` or `wwwroot/appsettings.json` + `AddConfiguration`. For persisted telemetry, log to a backend API or the Application Insights JS SDK via interop — there is still no native WASM App Insights SDK.
- Never log secrets or PII — client-side logs are fully visible to the user.

## Performance, size, and startup

- Publish with IL trimming; install the `wasm-tools` workload so publish also does runtime relinking.
- **AOT** (`<RunAOTCompilation>true</RunAOTCompilation>`): roughly 2× download size for a large CPU-bound speedup — reserve it for compute-heavy apps; `<WasmStripILAfterAOT>true</WasmStripILAfterAOT>` claws back size. The default interpreter + jiterpreter is fine for most CRUD UIs.
- **Lazy-load assemblies** by route: `<BlazorWebAssemblyLazyLoad Include="Heavy.Feature.wasm" />` (Webcil means the extension is **`.wasm`, not `.dll`**) + `LazyAssemblyLoader.LoadAssembliesAsync` in `Router.OnNavigateAsync`.
- Publish output is precompressed (Brotli + gzip) — verify the host actually serves it (`Content-Encoding: br` in dev tools); static hosts without content negotiation need the `decode.min.js` + `loadBootResource` pattern.
- .NET 10 changes to respect: `blazor.boot.json` is gone (boot config is inlined into `dotnet.js`); `BlazorCacheBootResources` and Blazor's custom cache are removed (standard HTTP caching + fingerprinting instead); enable client-side fingerprinting/preloading with `<OverrideHtmlAssetPlaceholders>true</OverrideHtmlAssetPlaceholders>`; set the environment with `<WasmApplicationEnvironmentName>` (not the `Blazor-Environment` header).
- Customize the loading UI via the template's CSS custom properties (`--blazor-load-percentage`, `--blazor-load-percentage-text`).

## PWA

- Offline support only works **when published** — the dev `service-worker.js` is a no-op; always test published output.
- Register the worker with `navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' })` (the .NET 10 template default; recommended for every version).
- The cache-first atomic snapshot means users may run **any historical version** until all tabs close — never ship backward-incompatible API changes without a compatibility strategy.
- Offline users cannot authenticate or acquire tokens — design auth-dependent features accordingly. Adopt offline support deliberately; it adds real complexity.

## Forms and validation

- `EditForm` + `Model` (or `EditContext` for advanced control) + `DataAnnotationsValidator` + `ValidationSummary`/`ValidationMessage`; prefer `OnValidSubmit`.
- For FluentValidation or other third-party systems, use a custom validator component that manages a `ValidationMessageStore` against the cascaded `EditContext` in place of `DataAnnotationsValidator`.
- .NET 10 opt-in nested/collection validation: `builder.Services.AddValidation()` + `[ValidatableType]` on the root model; **model classes must live in `.cs` files, not `.razor`** (source-generator limitation); `[SkipValidation]` to exclude members.
- Client validation is UX, not security — the server API re-validates everything it receives.

## Testing

- Unit test components with **bUnit** + xUnit, run via `dotnet test`: render with `TestContext`, interact via `Find(...)`, assert with `MarkupMatches` (semantic HTML comparison — stable against whitespace churn). Mock `IJSRuntime` and injected services (Moq/NSubstitute).
- Use **Playwright for .NET** for end-to-end tests when behavior depends on real DOM manipulation or hard-to-mock JS libraries.

## Outdated patterns — never suggest these

- The "ASP.NET Core Hosted" WASM template or Client/Server/Shared solution layout (removed in .NET 8).
- `@rendermode` / `InteractiveWebAssembly` / `InteractiveAuto` / prerendering in a standalone app — Blazor Web App concepts only.
- `ProtectedLocalStorage`/`ProtectedSessionStorage` in WASM (server-side only).
- `IJSUnmarshalledRuntime` (obsolete — use `[JSImport]`/`[JSExport]`).
- The OIDC implicit grant flow (PKCE code flow only).
- `.dll` names in `BlazorWebAssemblyLazyLoad` items (Webcil uses `.wasm`).
- References to `blazor.boot.json` or `BlazorCacheBootResources` (removed in .NET 10).
- Synchronous reads on HTTP response streams (streaming is the .NET 10 default).
- Registering the PWA service worker without `{ updateViaCache: 'none' }`.
