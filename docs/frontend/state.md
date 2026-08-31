# State

All non-trivial client state lives in NgRx Signal Stores. There are no actions, reducers or effects;
the Events plugin is used deliberately and narrowly.

## Conventions

- One store per entity type, `withEntities` for keyed collections.
- `protectedState` stays on. State is written only through `patchState` with standalone updaters.
- `rxMethod`, not `signalMethod`, wherever requests can overlap — `switchMap` is what prevents a
  stale response overwriting a fresh one.
- Loading is declarative: one `rxMethod` wired in `onInit` to a signal computation, so a change of
  input reloads and races resolve themselves.

`ConversationListStore` (`core/conversations/conversation-list-store.ts`) is the reference list
store. Copy its shape rather than re-deriving one.

## The composable features

Five `signalStoreFeature`s in `core/state/`, each with a co-located spec.

| Feature | Adds |
| --- | --- |
| `withRequestStatus` | One `requestStatus: 'idle' \| 'pending' \| 'fulfilled' \| { error: AppError }` plus derived flags. One field rather than `isLoading` + `error`, so "loading and errored" is unrepresentable; the error arm carries the whole `AppError` because surfaces render its `traceId`. |
| `withOffsetPagination` | `skip` / `take` / `totalCount` over the API's `PaginatedResponseDto`, with `MAX_PAGE_SIZE = 100`. `skip` advances by items actually returned and `hasMore` compares against `totalCount`, so clamped page sizes and exact-multiple totals need no special case. |
| `withPendingIds` | `pendingIds: ReadonlySet<string>` for per-row in-flight tracking. Updaters always **replace** the Set — mutating it returns the same reference and `patchState` then performs no signal write at all. |
| `withClientQuery` | Client-side search and sort with a configurable `searchableText`, per-key comparators, and an `isAuthoritative` gate so sort is only offered over a complete set. |
| `withResetOnSignOut` | Snapshots initial state via `withProps` and restores it on the signed-out event. |

### `withResetOnSignOut` must be composed last

It snapshots `getState` during construction, so any state-contributing feature composed *after* it
would survive sign-out. A dev-only `onInit` guard throws naming the offending keys, because the type
system cannot catch it.

It is event-driven rather than called from a service because route-scoped stores are unreachable
from a root service.

**Clearing state is not cancelling work.** A store that loads asynchronously must also
`takeUntil(injectSignedOut())`, or an in-flight response lands after the reset and repopulates the
previous user's data.

## Cross-store events

`@ngrx/signals/events` is an opt-in, used only where one store must learn something another store
did. The rule is **server-confirmed facts only** — optimistic patches and their rollbacks stay
inside the store that made them.

| Group | Carries |
| --- | --- |
| `sessionEvents` | `signedOut` |
| `conversationEvents` | `updated`, `deleted` |
| `projectEvents` | `favorited` |
| `turnEvents` | Turn-scoped facts the shell needs |
| `mcpEvents` | Credential state changes |
| `adminEvents` | Three user events carrying a DTO, plus `modelCatalogChanged` and `mcpCatalogChanged` as `type<void>()` |

The two catalog events are `void` on purpose, because the thing that changed is not describable from
the response: a save that sets `isDefault` demotes a row no response mentions, and a 204 from a
delete carries no `dateDeactivated`. Each has two observers — one route-scoped, one root — and both
refetch.

The one bounded exception to "server-confirmed facts only" is the favourite endpoint, whose 204
carries no body; the event carries the fact rather than a DTO.

## The stores

**Root-scoped** — session, UI preferences, toasts, the model and MCP catalogs and their action
stores, conversation list/actions/export, project lookup and actions, document download and
supported extensions, user actions, pending prompt, turn settings.

**Component- or route-scoped** — the conversation and turn stores on `/chat`, the four library list
stores, the project detail trio provided on the route, the four admin screen stores, the composer's
upload and dictation stores.

Two scoping choices worth knowing:

- `UiStore` deliberately does **not** reset on sign-out. Sidebar width and pinned projects are
  device-local preferences, not the signed-in user's data.
- `AdminReportsStore` is provided on the *screen*, not the admin layout, so back-navigation gets a
  fresh empty instance rather than a stale report.
- `UploadStore` is provided by `Chat` and again by the project files panel, giving two independent
  instances rather than one shared queue.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/app/core/state/` | The five features and their specs |
| `enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts` | The reference list store |
| `enterprise-gpt-ui/src/app/core/events/` | The six event groups |
| `enterprise-gpt-ui/src/app/core/session/session-store.ts` | Identity and permissions |
| `enterprise-gpt-ui/src/app/core/state/with-reset-on-sign-out.spec.ts` | The composition-order guard |

## Related

- [errors.md](errors.md)
- [../architecture/frontend.md](../architecture/frontend.md)
