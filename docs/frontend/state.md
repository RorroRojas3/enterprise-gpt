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

Five `signalStoreFeature`s, each with a co-located spec: four in `core/api/` (`withRequestStatus`,
`withOffsetPagination`, `withPendingIds`, `withClientQuery`) and `withResetOnSignOut` in
`core/session/`.

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
| `projectEvents` | `created`, `updated`, `deleted`, `favorited` |
| `turnEvents` | `completed` (only on a folded `Finished`, with `wasFirstTurn`) and `interrupted` (a cut-off, or a failure while the turn is live; never Stop or an abort). Published by a chat session's `TurnStore`, which root stores cannot reach |
| `mcpEvents` | Credential state changes |
| `adminEvents` | `userCreated` and `userUpdated` carrying a DTO, `userDeactivated` carrying an id, plus `modelCatalogChanged` and `mcpCatalogChanged` as `type<void>()` |

The two catalog events are `void` on purpose, because the thing that changed is not describable from
the response: a save that sets `isDefault` demotes a row no response mentions, and a 204 from a
delete carries no `dateDeactivated`. Each has two observers — one route-scoped, one root — and both
refetch.

`ConversationActivityStore` observes `turnEvents`. It marks endings that happen out of sight, and
handles the first-turn name: on `completed` with `wasFirstTurn` it fetches the conversation detail,
calls `ConversationListStore.refreshRow`, and emits `conversationEvents.updated` carrying the list
fields. A conversation that finished in the background is therefore named in the sidebar, and the
open screen's `ConversationStore` hears the same event. `ChatSessions` listens to
`conversationEvents.deleted` to destroy a deleted conversation's session.

The one bounded exception to "server-confirmed facts only" is the favourite endpoint, whose 204
carries no body; the event carries the fact rather than a DTO.

## The stores

A store lives where it is used. One that only a single feature injects, root-scoped or provided on a
page or route, sits in that feature's folder (`features/shell/ui-store.ts`,
`features/chat/turn-store.ts`, `features/admin/users/user-actions-store.ts`). One that more than one
feature injects, or that a guard, interceptor or core service injects, sits in the `core/` topic it
serves, because `core` may not import a feature: `SessionStore`, `ModelCatalogStore`,
`McpCatalogStore`, `ConversationListStore`, `ProjectLookupStore` and `ToastStore`, plus the
conversation, project and document action stores and the composer's turn-settings and pending-handoff
stores. `ConversationActivityStore` is one of them: the shell's sidebar and navbar read it, so it
cannot live in `features/chat/`.

**Root-scoped** — session, UI preferences, toasts, the model and MCP catalogs and their action
stores, MCP credentials, conversation list/actions/export, conversation activity, project lookup and actions,
document download, supported extensions, user actions, pending prompt and pending attachments, turn
settings, and `ChatSessions` (`features/chat/chat-sessions.ts`), the registry of chat sessions.

**Chat-session-scoped** — one `TurnStore` and one `UploadStore` per conversation, owned by an
environment injector that `ChatSessions` builds and destroys. They outlive the `/chat` screen, so a
turn keeps streaming while the user reads another conversation; see
[../conversations/turn-lifecycle.md](../conversations/turn-lifecycle.md#one-session-per-conversation).

**Component- or route-scoped** — the conversation store on `/chat`, the four library list stores,
the project detail trio provided on the route (`ProjectConversationsStore` is also provided by each
expanded sidebar project), the four admin screen stores, the composer's dictation store, and the
project screen's two `UploadStore` instances.

Two scoping choices worth knowing:

- `UiStore` deliberately does **not** reset on sign-out. Sidebar width and pinned projects are
  device-local preferences, not the signed-in user's data.
- `AdminReportsStore` is provided on the *screen*, not the admin layout, so back-navigation gets a
  fresh empty instance rather than a stale report.
- `UploadStore` is never shared by accident. Each chat session owns one instance, and
  `ChatSessionView` points the composer's `COMPOSER_UPLOADS` token at it (`useExisting`), so the
  chips the composer draws are the very ones that session's `TurnStore` gates a send on, and another
  conversation's uploads can never release that gate. `ProjectDetail` provides two:
  one project-bound, backing the Files tab, and a second, separate instance behind `COMPOSER_UPLOADS`
  (`useClass`) for its composer — deliberately unbound, so a file attached there becomes a
  conversation document once the prompt creates one, not a document of the project the screen is
  showing. See [../admin/projects.md](../admin/projects.md#the-client). `ChatSessions` calls
  `deferUploadsUntilSend()` on every session's instance, so attaching only states an intention and
  `TurnStore` posts the files at the head of its send pipeline; the project composer's instance has
  no target, so nothing starts there either. The Files-panel instance keeps the opposite default,
  where dropping a file is itself the instruction. Cancelling a busy chip calls `UploadCancelClient`
  (`core/uploads/`) — a plain root-scoped `@Injectable`, not a store, since a fire-and-forget
  `DELETE` renders nothing — so the request outlives a screen a cancel very often navigates away
  from.
- `PendingAttachmentsStore` (`core/composer/`) mirrors `PendingPromptStore`: root-scoped, single-shot,
  id-matched — the same shape, for the same reason, one field wider. It carries the `File` handles a
  project-created conversation's composer already held, from the screen that creates the
  conversation to the one that uploads into it.

## Conversation activity

`ConversationActivityStore` (`core/conversations/`, root) answers "what is each conversation doing,
and what did I miss?" for the sidebar, the library, the navbar and the composer. It does not own the
turns. `core/` may not import `features/chat/`, so `ChatSessions` hands it a port through
`connect()` — the running ids, the running count, the conversation on screen, and a `stop(id)` —
and the store reads those signals.

- **State is the `unseen` map**, conversation id to `answer` or `interrupted`. A turn that ends
  while its conversation is on screen adds nothing; one that ends elsewhere marks it, and
  `markSeen(id)` clears the mark when the conversation is opened or deleted.
- **Reads are derived.** `activityOf(id)` reads one conversation; `running` outranks `answer`, which
  outranks `interrupted`. `summary` reduces the whole set to the one mark a collapsed control shows,
  and `summarySuffix` to the phrase appended to its name, for example " — 2 generating, 1 new
  answer".
- **An answer that finishes out of sight raises an info toast**, "Answer ready" with the
  conversation's name and an Open action. It is keyed per conversation, so a later answer replaces
  it, and seeing the conversation clears it. The name comes from the sidebar's copy; when the sidebar
  does not hold the conversation, or the turn was the first and the row still carries the
  placeholder, the store reads it from the API first. An interrupted ending raises no toast, only the
  row's mark.
- **`atCapacity`** is `runningCount >= maxConcurrentTurns`; see
  [../conversations/turn-lifecycle.md](../conversations/turn-lifecycle.md#concurrency).

`ToastStore` takes an optional `action: { label, run }` on `ToastInput` and renders at most one
button for it; `onRetry` stays as shorthand for an action labelled Retry, and `action` wins if both
are given. The stored `Toast` carries `actionLabel`; each item emits `actioned`, which the region
passes to `runAction(id)`, and that dismisses the toast before it calls the handler, so a toast the
handler raises does not stack under the one just used. A `key` replaces a live toast with the same
key, and `dismissKey(key)` clears it. Every toast dismisses itself after `AUTO_DISMISS_MS` (three
seconds), whatever its tone.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/app/core/api/with-*.ts` | Four of the five features and their specs |
| `enterprise-gpt-ui/src/app/core/session/with-reset-on-sign-out.ts` | The fifth, which resets a store on sign-out |
| `enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts` | The reference list store |
| `enterprise-gpt-ui/src/app/core/events/` | The six event groups |
| `enterprise-gpt-ui/src/app/core/conversations/conversation-activity-store.ts` | Running and unseen endings, the cap, the "Answer ready" toast |
| `enterprise-gpt-ui/src/app/features/chat/chat-sessions.ts` | The registry that owns each conversation's turn and upload stores |
| `enterprise-gpt-ui/src/app/core/session/session-store.ts` | Identity and permissions |
| `enterprise-gpt-ui/src/app/features/` | Each feature's own stores, beside the screens that provide them |
| `enterprise-gpt-ui/src/app/core/session/with-reset-on-sign-out.spec.ts` | The composition-order guard |
| `enterprise-gpt-ui/src/app/core/conversations/conversation-activity-store.spec.ts` | Unseen marks, the toast, summaries and the cap |
| `enterprise-gpt-ui/src/app/features/chat/chat-sessions.spec.ts` | Session lookup, retention and disposal |

## Related

- [errors.md](errors.md)
- [../architecture/frontend.md](../architecture/frontend.md)
