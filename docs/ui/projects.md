# Projects

The `/projects` area: a grid that drains rather than pages, a lifecycle store whose one modal serves three entry points, standing instructions guarded against a navigation that would lose them, a files panel that added no upload code at all, and a composer that deliberately refuses to stream — plus the structural move that made the last of those possible, which is the largest thing EP-9 shipped and the least visible.

Audience: a developer adding US-307's project picker or US-908's conversations tab, putting the shared composer on a third surface, or debugging a project write that clears a field nobody edited. Read [Conversation Library](conversation-library.md) first for the list-screen conventions this grid copies — the URL contract, the route-scoped store, the empty states — [File Attachments](file-attachments.md) for the upload machinery §7 reuses wholesale, and [Frontend Foundation](frontend-foundation.md) for the composable store features and `core/errors` conventions everything here composes. Bare `§` references below are to sections of _this_ page.

The server side is the authority on everything past the request: [Projects](../projects/project-management.md), and [Document Upload and Ingestion](../documents/upload-workflow.md) for the pipeline a project file goes through.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Five stories and one prep step, which is where the surprise is: the prep step is bigger than any of the stories.

| Story                | What it delivers                                                                                                                                                                                             |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **prep**             | The composer stack moved to `shared/composer/` behind a `COMPOSER_HOST` token, `UploadStore` moved to `core/documents/`, and a `no-restricted-imports` rule so the layering that forced both is machine-checked |
| **US-901**           | Frame `4c`'s card grid, server-side `?name=` search on US-701's exact URL contract, and a **drain** — `take=100` to a 500-project ceiling — with the shortfall stated rather than silent                       |
| **US-903**           | Create, rename, describe and delete, from one root store and one modal; the client's own duplicate-name copy; a delete that releases the project's conversations across two other stores                       |
| **US-904**           | The project detail screen the rest of the epic hangs off — header, composer slot, tab strip — with tabs as **real child routes**, and standing instructions behind a `canDeactivate` warning                   |
| **US-905**           | A files panel that binds `{ kind: 'project', id }` and reuses EP-8's transport, staged poll, refusals and download unchanged                                                                                    |
| **US-906**           | A prompt typed on a project creating the conversation **inside** it — and then handing the turn to the chat route, because that is where the transcript is                                                     |

Nine decisions shape everything here, and each looks removable until you know what it prevents:

1. **The composer moved to `shared/` behind an injection token.** Frame `4e` puts the shared composer on the project screen, but it lived in `features/chat/` and injected the route-scoped `TurnStore`, and dependencies run one way. `Chat` provides `TurnStore` as the host; the project screen provides `ProjectComposerHost` (§3).
2. **The project screen does not stream.** US-906's criterion is that the turn streams _as it does on the chat route_ — and the transcript is there. So a send creates the conversation, holds the prompt in a root `PendingPromptStore`, and navigates (§8).
3. **The prompt travels in a store, not in the URL.** A prompt in the address bar is a prompt in the history and in every referrer header the page sends (§8.2).
4. **The grid drains rather than pages.** No endpoint here accepts a sort parameter, so US-902's sort is only ever honest over a fully materialised set — and a grid that sorted its first page would put a second sorted run underneath it on the next fetch (§4.1).
5. **`toProjectBody` is the only place an update body is built.** `PUT api/projects/{id}` is a full representation: omitting `description` or `instructions` _clears_ them. The edit dialog and the instructions panel go through the same function (§5.2).
6. **`beginEdit` fetches before opening.** `ProjectSummaryDto` omits `instructions` **entirely** — absent, not null — so a form seeded from a grid card would PUT `instructions: null` over text the user never saw (§5.3).
7. **The duplicate-name message is the client's.** The server sends `A project named 'X' already exists.`; the board and the criterion require "You already have a project with this name." One message is swapped; every other `Name` failure renders verbatim (§5.4).
8. **A project delete releases its conversations, and the client must mirror that.** The server sets `ProjectId = null` on them, and `toUpdateBody` echoes `projectId` on every rename — so a client row still holding the dead id takes a 404 on its next rename, a failure with nothing to do with projects (§5.5).
9. **US-905 added no upload code.** `UploadTarget` was already a discriminated `conversation | project` pair, so the panel binds the project arm and reuses the lot. The one change was generalising `bindConversation` into a private `bindTarget` (§7.1).

### 1.1 Where each piece lives

| Concern                                                              | Where                                                                                                                                                                                                                                            |
| -------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The wire shapes — `ProjectSummaryDto`, `ProjectDto`, `ProjectWriteBody`, `ProjectDocumentDto`, `PROJECT_FIELD_LENGTHS` | [`domain/api/project.ts`](../../enterprise-gpt-ui/src/app/domain/api/project.ts) — framework-free                                                                                          |
| "2h ago" / "Yesterday" / "Aug 5"                                     | [`domain/format/relative-time.ts`](../../enterprise-gpt-ui/src/app/domain/format/relative-time.ts) — framework-free                                                                                                                               |
| The one place an update body is built                                | [`core/projects/project-update.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-update.ts)                                                                                                                                              |
| The duplicate-name swap                                              | [`core/projects/duplicate-name.ts`](../../enterprise-gpt-ui/src/app/core/projects/duplicate-name.ts)                                                                                                                                              |
| Create, rename, describe, delete, and the dialogs' state             | [`core/projects/project-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-actions-store.ts) — root-scoped                                                                                                                  |
| The four events three scopes listen on                               | [`core/events/project-events.ts`](../../enterprise-gpt-ui/src/app/core/events/project-events.ts)                                                                                                                                                  |
| What executes a prompt, as a token                                   | [`core/chat/composer-host.ts`](../../enterprise-gpt-ui/src/app/core/chat/composer-host.ts)                                                                                                                                                        |
| The prompt handed from one screen to another                         | [`core/chat/pending-prompt-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/pending-prompt-store.ts) — root-scoped, single-shot                                                                                                               |
| The history rule both search screens share                           | [`core/navigation/search-history.ts`](../../enterprise-gpt-ui/src/app/core/navigation/search-history.ts)                                                                                                                                          |
| The composer, model menu, tools menu, dictation                      | [`shared/composer/`](../../enterprise-gpt-ui/src/app/shared/composer/)                                                                                                                                                                            |
| The upload transport's store                                         | [`core/documents/upload-store.ts`](../../enterprise-gpt-ui/src/app/core/documents/upload-store.ts) — [File Attachments](file-attachments.md)                                                                                                      |
| The area's route table and the layout that mounts its dialogs        | [`features/projects/projects.routes.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.routes.ts), [`projects-layout.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects-layout.ts)                                     |
| The grid and its drained list                                        | [`features/projects/projects.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.ts), [`project-list-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/project-list-store.ts)                                             |
| The two dialogs                                                      | [`features/projects/dialogs/`](../../enterprise-gpt-ui/src/app/features/projects/dialogs/)                                                                                                                                                        |
| The detail screen, its route table and its stores                    | [`features/projects/detail/`](../../enterprise-gpt-ui/src/app/features/projects/detail/)                                                                                                                                                          |
| The composer host that does not stream                               | [`features/projects/detail/project-composer-host.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-composer-host.ts)                                                                                                          |
| The unsaved-instructions guard                                       | [`features/projects/detail/instructions/unsaved-instructions.guard.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/instructions/unsaved-instructions.guard.ts)                                                                     |
| Fixtures for specs                                                   | [`src/testing/projects.ts`](../../enterprise-gpt-ui/src/testing/projects.ts)                                                                                                                                                                      |

## 2. Quick start

### 2.1 Putting the composer on a new screen

The composer no longer knows what runs a prompt. Provide a store that satisfies `ComposerHost`, point the token at it, and render the component:

```ts
@Component({
  providers: [MyHost, provideComposerHost(MyHost)],
  imports: [Composer],
  template: `<app-composer />`,
})
export class MySurface {}
```

`provideComposerHost` is a function rather than a `{ provide, useExisting }` literal on purpose: `useExisting` is typed `any`, so a store that quietly stopped satisfying the contract would fail at runtime. The function's `Type<ComposerHost>` parameter makes it a compile error instead.

`ComposerHost` is six members — `composerSeed`, `inFlight`, `turnError`, `consumeComposerSeed()`, `send()`, `stop()` — deliberately not `TurnStore`'s whole API. A host that cannot stream reports `inFlight` for its own request, keeps `turnError` null, and has nothing to stop.

`UploadStore` must be provided on the same injector or above it; the composer injects it directly for the paperclip and the drop zone.

### 2.2 Building a project update body

Never by hand. `PUT api/projects/{id}` assigns every field unconditionally:

```ts
// Rename only. `description` and `instructions` are echoed from the project as it stands.
_update({ target, body: toProjectBody(target, { name }) });

// Clear the description, leave everything else.
_update({ target, body: toProjectBody(target, { description: null }) });
```

`undefined` means "leave it"; `null` means "clear it". They cannot be collapsed with `??` — the server distinguishes them, and collapsing would make clearing a description impossible.

### 2.3 Uploading into something that is not a conversation

`UploadStore` has been target-agnostic since EP-8. A project surface binds the project arm and gets the transport, the concurrency cap, the staged poll, the four refusal shapes and the download for free:

```ts
this._uploads.bindProject(projectId);
this._uploads.attach(files);
```

`bindProject` is a plain method rather than a `signalMethod` because a project surface only renders under an already-resolved `:projectId` — there is no absent state to model. `bindConversation` stays a `signalMethod` because `/chat` genuinely has none.

### 2.4 Rules that are not style preferences

- **Never import from `@features/*` in `shared/`, `core/` or `domain/`.** ESLint now fails the build for it, with the message naming the escape hatch (§3.4).
- **Never build a project write body outside `toProjectBody`.** A `{ name }` body clears the description and the standing instructions (§5.2).
- **Never open the edit form on a `ProjectSummaryDto`.** Call `beginEdit`, which decides whether a fetch is needed by testing for the **key**, not its value (§5.3).
- **Never put a prompt in a query parameter.** `PendingPromptStore` exists for exactly this (§8.2).
- **Never remove `releaseProject` from the project-delete path.** The defect it prevents is invisible until a rename (§5.5).
- **Never add a Load more to the projects grid.** It drains on purpose, and the ceiling is stated (§4.1).

## 3. The prep step: the composer moved down a layer

### 3.1 Why a screen forced a refactor

Frame `4e` draws the **shared** composer on the project screen — the same prompt box, model pill, tools pill, microphone and paperclip the chat route renders. The component lived in `features/chat/composer/` and injected `TurnStore`, which is provided by the `Chat` route. Dependencies run one way in this app:

```text
features → shared → core → domain
```

`features/projects` importing `features/chat` is not a direction that exists, and it is not a rule for its own sake: two lazy features importing each other means the chunk splitter gives up and everyone who opens either route downloads both.

So three things moved, and one thing was invented:

| Moved                          | From                    | To                  |
| ------------------------------ | ----------------------- | ------------------- |
| `Composer`, `ModelMenu`, `ToolsMenu`, `DictationStore` | `features/chat/composer/` | `shared/composer/`  |
| `UploadStore`                  | `features/chat/`        | `core/documents/`   |
| `shouldReplaceHistory`         | `features/conversations/` | `core/navigation/` |

### 3.2 `COMPOSER_HOST`

The move exposed the actual coupling. The composer did not need `TurnStore`; it needed *something* that takes a prompt, reports whether a request is running, hands text back, and can be stopped. Naming that as a token is what lets the component live in `shared/` at all:

| Route          | Host                  | What `send()` does                                                    |
| -------------- | --------------------- | ---------------------------------------------------------------------- |
| `/chat`        | `TurnStore`           | Opens the SSE stream in place                                          |
| `/projects/{id}` | `ProjectComposerHost` | Creates a conversation inside the project and navigates to `/chat/{id}` |

`ProjectComposerHost` carries a compile-time proof that it satisfies the contract:

```ts
export type ProjectComposerHostSatisfiesContract =
  InstanceType<typeof ProjectComposerHost> extends ComposerHost ? true : never;
```

`turnError` is typed as a bare `object | null` rather than as `TurnStore`'s notice type, because the composer only ever asks whether there **is** one. Widening it would drag `features/chat` types into `core/` and undo the whole exercise.

### 3.3 `UploadStore` moved for the same reason

The composer injects it, and so does US-905's files panel — two features, so it cannot live in either. Nothing about the store changed in the move; §7.1 covers the one method it gained.

### 3.4 The layering rule is now machine-checked

A single `@features/…` import from `shared/` or `core/` would undo all of this **silently**: it type-checks, it builds, and it shows up only as a lazy chunk the whole app suddenly downloads. So `eslint.config.mjs` bans the pattern under `src/app/{shared,core,domain}/`, with a message that names the escape hatch rather than just the prohibition:

> Dependencies run one way: features → shared → core → domain. Move the shared piece down a layer, or invert it with an injection token (see COMPOSER_HOST).

**Specs are exempt on purpose.** `shared/composer/composer.spec.ts` drives the real `TurnStore`, because what it verifies — the Send/Stop morph, the seed handoff, the focus fixups — are reactions to state a stub would have to fake into existence. A test file is not part of the application graph.

## 4. The grid (US-901)

`/projects` renders frame `4c`: a title, a toolbar, and a responsive card grid (`col-12` / `col-md-6` / `col-xl-4`). It copies `ConversationLibraryStore` — route-scoped, URL-driven, `rxMethod` + `switchMap` so a slow `"data"` cannot paint over a fast `"data platform"`, no `debounceTime` because `SearchInput` already debounces 300 ms and emits only committed values, `takeUntil(signedOut$)` on the request because [`withResetOnSignOut` clears state and does not cancel work](frontend-foundation.md#55-withresetonsignout--compose-it-last), and that feature composed last — with one structural difference.

### 4.1 It drains rather than pages

There is no Load more here and there never will be:

```ts
export const PROJECT_PAGE_SIZE = 100; // the server's own clamp ceiling
export const PROJECT_LOAD_CEILING = 500; // the PRD's regime A — five requests at most
```

`GET api/projects` accepts **no sort parameter**. US-902's sort is therefore only honest over a set the client holds in full: a grid that sorted its first page would put a second sorted run underneath it the moment another page arrived. So each query walks the pages itself with `expand`, up to a stated ceiling.

Four details in the drain are load-bearing:

- **`expand`, not a self-referential `concat`.** Each iteration completes and is dropped, so a five-page drain does not retain five nested subscribers — the same reason `UploadStatusClient.follow` uses it.
- **`_querySuperseded$` cancels the whole chain.** `switchMap` cancels the one request in flight when a new query arrives; the drain is a _sequence_ of them. Without the subject, a page fetched against the old term lands after the new query's `setAllEntities` and appends rows from a list that is gone.
- **`truncated` is written where the drain stops, never in a `finalize`.** A `finalize` also runs on the cancellation a new query or a sign-out causes, and would stamp a partial load as a truncated one.
- **A failure mid-drain replaces the grid with the error panel.** A half-drained set is neither complete nor knowably incomplete, and showing it is precisely the silently-partial list regime A exists to avoid. Retry re-runs from the first page.

The ceiling notice reads:

> Showing the 500 most recent of 3,412 projects.

That is frame `4g`'s neutral wording, **not** frame `4d`'s. `4d`'s sentence names sorting — "Sorting is unavailable past the 500-project load ceiling" — and there is no sort control on screen until US-902, so it would explain a control the reader cannot see. US-902 swaps it back when the select arrives.

`withClientQuery` is deliberately **not** composed yet. Search here is server-side, and no sort control ships until US-902 — which is the feature's consumer and wires it over `entities` with `isAuthoritative: isFulfilled() && isFullyLoaded()`.

### 4.2 The URL contract, shared with the conversations library

Identical to [Conversation Library §3](conversation-library.md#3-the-url-is-the-source-of-truth), down to the function: `?name=` is bound by `withComponentInputBinding()` and drives the store; `onSearched` is the only writer and it writes to the router; `[value]` is bound one-way; nothing navigates from an effect over `name()`.

`shouldReplaceHistory` moved to `core/navigation/search-history.ts` when this screen needed it — `features → features` is not a direction this app has, and a second copy is a second thing to get wrong. `features/conversations/conversations-route.ts` now re-exports it so that screen's route contract still reads from one file.

The grid's own route file keeps the parameter name:

```ts
export const NAME_PARAM = 'name';
export { shouldReplaceHistory } from '@core/navigation/search-history';
```

### 4.3 The screen

- **Two empty states, not three.** `hasNoMatches()` gets frame `4b`'s ridgeline treatment with the term named and a **Clear search** action; `hasNoProjects()` gets "No projects yet" and a **New project** action. There is no favourites filter here to need a third.
- **The heading reads `projects.name()`, never the component's `name()`.** The store's copy is the one the rendered cards belong to, so the heading cannot name a term whose results have not arrived.
- **"Updated …" labels are computed once per result set**, from a `Date.now()` sampled inside a `computed`. A clock read from a template binding is a fresh value on every change-detection pass, which `provideCheckNoChangesConfig({ exhaustive: true })` compares between passes — and would recompute every label on every unrelated render. Labels are minute-granular at their finest, so nothing visible is lost.
- **The card's link is a string, not a commands array.** `ProjectCard.link` is a signal `input()`, so a fresh `[route, id]` literal fails `Object.is` every pass, writing the input, marking every card dirty and re-running `RouterLink`'s href update. At the 500-project ceiling this store deliberately materialises, that is OnPush defeated across the whole grid. It is also **absolute**, because this component's own route is a path-less child and a relative `[project.id]` would resolve against whichever route happened to host the card.
- **The kebab items are `aria-disabled`, never natively `disabled`.** The native attribute blurs the item the instant it is pressed, dropping a keyboard reader onto `<body>` for the whole round trip. `beginEdit` and `beginDelete` both refuse on the pending id, so the guard is real either way.
- **The star is withheld** — `[showFavorite]="false"` — because `ProjectSummaryDto` carries no flag until US-909 and a control that can be pressed and never persisted is worse than one that is not there. The **sort select** is absent for the same reason at one remove (§11).
- **Focus is repaired when the last card leaves.** `beginDelete` arms the fixup at the gesture rather than inferring it from the grid emptying — the removal is confirmed elsewhere, so by the time the card goes there is nothing left to tell the screen the reader caused it. The search field is the target, because it is the only control outside every branch of the template. Only when focus actually fell through.

## 5. Lifecycle (US-903)

### 5.1 One root store, one modal, three entry points

`ProjectActionsStore` is root-scoped for the reason `ConversationActionsStore` is: `rxMethod` binds its subscription to the injector that created it, and a delete **navigates away from** `/projects/{id}` — a route-scoped store would have the request torn down mid-flight, leaving nothing deleted and nothing said.

Its boundaries are the ones the conversation store documents, and they hold for the same reasons:

- **Entity writes travel as events**, never as `patchState` into another store's protected state.
- **Validation problems render inline** in the dialog and are never toasted; everything else goes through `ToastStore.fromError`.
- **Events carry server-confirmed facts only.** Creation is not optimistic — it mints a server id — and an update adopts the DTO the server returned rather than a local guess at `dateModified`.

Concurrency is chosen per flow and each choice means something:

| Flow                   | Operator     | Why                                                                                                                     |
| ---------------------- | ------------ | ------------------------------------------------------------------------------------------------------------------------- |
| create / update        | `exhaustMap` | A second submit while one is in flight is a double-click, not a second project — and two would then collide on the name |
| load-for-edit          | `switchMap`  | A second kebab opened on another card replaces the first; the in-flight response would otherwise fill the dialog with the project the user navigated away from |
| delete                 | `mergeMap`   | Deletes of different projects are independent and may overlap; `exhaustMap` would silently drop the second              |

`beginCreate(afterCreate?)` takes a callback rather than raising an event, because US-903's third criterion is that the composer's invoker returns the user _exactly where they were_ with the new project selected. That answer belongs to one caller; `projectEvents.created` is already carrying the fact to everyone else. It fires **after** the dispatch, so a caller that navigates finds the lists already holding the row it is navigating to.

Two flags are easy to conflate and are deliberately separate:

- **`formBusy` tracks the request, not the dialog.** A dialog force-closed mid-flight (a second Escape is uncancelable in Chromium) leaves it true until the request settles, so a reopened dialog honestly reports that `exhaustMap` is still occupied rather than swallowing the next submit silently. `cancelForm()` does not clear it.
- **`formLoading` is the edit fetch** (§5.3), and it disables every field through one root-level `disabled()` schema rule.

`ProjectFormDialog` is a Signal Form and copies `RenameConversationDialog`, including the part that is easy to miss: the server's verdict arrives through a declarative `validate` rule that reports it only while the field still holds the exact (trimmed) name the server rejected. That is what clears the inline message on the next keystroke — Signal Forms has no `setErrors`, so an imperative reset would have nothing to reset. `unmatchedServerMessages` renders anything keyed to a property the form does not model, because a server message the user cannot see is a save that silently fails.

### 5.2 `toProjectBody` is the only place an update body is built

`UpdateProjectActionDto` assigns every field unconditionally, and **there is no PATCH**. A caller that sent `{ name }` alone would clear the project's description *and* its standing instructions — the thing US-904 exists to keep.

Both writers go through the same function with different arguments:

| Writer                                | Call                                                   |
| ------------------------------------- | -------------------------------------------------------- |
| The edit dialog (name + description + instructions) | `toProjectBody(target, changes)`                |
| The instructions panel (US-904)       | `toProjectBody(target, { instructions })`                |

An untouched optional field is sent as `null`, not `""`: the column is nullable, and a round trip that turned an absent description into an empty string would make "unchanged" look like an edit on the next comparison.

Note the id is **not** in the body — `PUT api/projects/{id}` carries it in the route, unlike `PUT api/conversations`, which carries it in the body.

### 5.3 `beginEdit` fetches before opening

`ProjectSummaryDto` omits `instructions` **from the JSON entirely** — the property is absent, not null, because the listing projection omits the column server-side. Seeding a form from a grid card and saving would therefore PUT `instructions: null` over text the user never saw.

So `beginEdit` accepts either shape and decides by testing for the key:

```ts
// The key, not its value: `instructions` is legitimately null on a project without any,
// and the listing omits the property rather than nulling it.
if (!('instructions' in project)) {
  _loadForEdit(project);
  return;
}
```

The dialog **opens on the click**, as the board draws it, and fills in when `GET api/projects/{id}` lands; the fields are disabled for that round trip, which is why the re-seed effect cannot destroy typing. A caller already holding a full `ProjectDto` — the detail screen's own header — opens immediately and costs nothing.

If that fetch fails the dialog **closes** rather than offering a form that cannot save correctly. Without the instructions there is nothing safe to PUT, and a form that looks editable and then clears a field is worse than one that never opened.

### 5.4 The duplicate-name message is the client's

`ProjectService.CreateDuplicateNameException` writes:

> A project named 'Helios' already exists.

Accurate, but it reads as a fact about the system rather than as something the reader can act on, and it quotes back a name already visible in the field above it. Frame `4h` and the acceptance criterion both require:

> You already have a project with this name.

The `errors` dictionary uses **one key for every name failure** — required, too long, already taken — so the shape has to be read off the message. `projectNameMessages` anchors a pattern on the two fixed halves the server's format string cannot lose, swaps that one message, and renders every other `Name` message verbatim:

- **Swapping rather than appending**, because two sentences saying the same thing under one field is worse than either alone.
- **Swapping only that one**, because "Names are limited to 256 characters." is the server's to word and nothing in the design contradicts it.
- A message that stops matching the pattern **falls through to verbatim**, which is the safe direction.

### 5.5 A delete releases its conversations — and the client must mirror it

`DeactivateProjectAsync` soft-deletes the project and its documents but sets `ProjectId = null` on every conversation it held: they survive as standalone chats keeping their own transcripts and their own uploads. Two consequences, and the second is the one that bites.

**The confirmation says what the API actually does.** A modal that said "and its conversations" would be asking the user to agree to something that will not happen.

**Every client-side copy of that id is now dead**, and `toUpdateBody` echoes `projectId` on every conversation rename. Left alone, the next rename of such a conversation would PUT a project the server has deleted and take a **404 for a request that has nothing to do with projects**. So `projectEvents.deleted` reaches two stores in another feature entirely:

| Store                   | What it does                                                        |
| ----------------------- | --------------------------------------------------------------------- |
| `ConversationListStore` | `releaseProject(id)` clears `projectId` on every held row             |
| `ConversationStore`     | Clears it on the open conversation, which outlives the row on a deep link |

Nothing renders `projectId` yet, which is exactly why this is easy to forget and worth stating: **the defect is invisible until US-307 or a rename, whichever comes first.**

That third-feature reach is also what puts this on the event bus rather than on a method call. `ProjectActionsStore` is root-scoped and knows nothing about conversations; `ProjectListStore` and `ProjectStore` are scoped to their routes. Four observers across three scopes is exactly the case a method call cannot serve.

Removal is **not** optimistic here, unlike a conversation delete: the same event releases conversations across two stores, and undoing _that_ on a failure would mean re-guessing which rows the server had reached.

### 5.6 Where the dialogs mount, and why not the shell

`ProjectsLayout` is to this area what `Shell` is to the signed-in app — it exists so `ProjectFormDialog` and `DeleteProjectDialog` mount **once**, driven by the root store, and both the grid's cards and a project's own header reach the same instance. A native `<dialog>` renders in the top layer, so their position in the template carries no visual meaning.

They are not in `Shell` itself, even though that is where the conversation dialogs live, because every invoker is still inside this feature. US-307 adds the first one outside it and is the story that should move them and pay the shell-chunk cost (§11).

The layout carries one non-obvious piece of housekeeping. The store is root-scoped and the dialogs are not; `Modal` closes its native element on destroy **without** emitting `closed`. A reader who leaves `/projects` with the create dialog open would leave `formMode` set in root state, and coming back would re-mount the dialog already open, holding the previous attempt. So `ProjectsLayout` cancels both on destroy. The conversation dialogs never hit this because `Shell` is not unmounted while signed in.

## 6. The detail screen and standing instructions (US-904)

Frame `4e`, in the board's own order, which is load-bearing: **header, then composer, then tabs.** Sending from the composer creates a conversation inside the project, so it belongs to the project rather than to whichever panel happens to be open.

### 6.1 Tabs are real child routes

```text
/projects
 └─ ''            ProjectsLayout           ← mounts both dialogs
      ├─ ''       Projects                 ← the grid
      └─ :projectId   providers: [ProjectStore, ProjectDocumentsStore]
           └─ ''  ProjectDetail
                ├─ ''             → redirect to 'instructions'
                ├─ 'instructions' canDeactivate: [unsavedInstructionsGuard]
                └─ 'files'
```

Two things follow from real routes that a signal holding a tab name cannot give: the panel a reader is on **survives a refresh and can be linked to**, and **`canDeactivate` has something to attach to** — which is how the unsaved-instructions warning fires on a move to the Files tab as well as on a move out of the project.

**Both stores are provided on the route, not on their panels.** The parent and every tab share one instance of each, `ProjectStore` is torn down when the reader leaves the project rather than when one panel unmounts, and the file count frame `4e` puts on the tab strip stays readable from the Instructions tab, where the files panel does not exist.

The count is `null` until the list has actually been read. "Files 0" on a project that has four is a worse answer than no count, and the strip renders before the panel it counts.

`ProjectStore` is the **only** source of `instructions`, which is why a detail request is not optional (§5.3). It is route-scoped so a second project's screen starts empty rather than showing the previous one's instructions while its own request flies, and both it and `ProjectDocumentsStore` bind a **computation** rather than a value, so navigating between two projects re-runs them and `switchMap` cancels what the reader left behind.

A delete performed anywhere — this screen's own kebab, the grid in another tab — sets a `deleted` flag that the component watches and navigates on. A store may not navigate; the flag is the seam.

### 6.2 Saving is not optimistic

The field settles into its saved value only once the server's `ProjectDto` comes back. The criterion says so, and the reason is that instructions reach the model **on every turn of every conversation in the project**: a value the client believed and the server rejected would silently change how the whole project answers.

The draft is a `linkedSignal` over the saved value, so the field re-seeds when the server confirms and when the reader navigates to another project. A `form()` would buy nothing — one field, one rule, and the cap is a native `maxlength` the textarea enforces itself, which stops the over-long value from being typed rather than reporting it afterwards.

`saving` is deliberately **not** narrowed to saves this panel started. `ProjectActionsStore` runs create and update through one `exhaustMap`, so a rename begun from the header would drop a Save pressed under it. Disabling the field for that round trip is the honest reading: both writers PUT the same full representation, and letting them race would mean one silently overwriting the other's echo.

### 6.3 The guard, and the one case that resolves without a modal

`unsavedInstructionsGuard` is a two-line `CanDeactivateFn` that asks the panel. `canDeactivate` rather than a `beforeunload` listener, and deliberately only that: the criterion is about navigating away **inside** the app, which is where the edit is genuinely recoverable and where a modal can name what is about to be lost. A browser unload prompt is unstyled, unnameable and increasingly ignored by engines.

Returning `false` is safe here, which it is not everywhere in this app — the authentication guards must return a `UrlTree`, because a cancelled navigation there leaves the startup shell on screen with nothing behind it. This one cancels back onto a fully rendered panel the reader is already looking at.

Three cases resolve `true` immediately, and the third is the interesting one:

| Case                     | Why no modal                                                                                       |
| ------------------------ | ---------------------------------------------------------------------------------------------------- |
| Nothing unsaved          | The common case, and it must not cost the reader a modal                                            |
| A save already in flight | The request belongs to the root store and outlives this panel, and the value being saved is the reader's |
| **The project was deleted** | Refusing would **trap** them: the screen is navigating away precisely because the project is gone, the PUT this edit would become is a 404, and cancelling leaves them on a URL that 404s on the next refresh — with no second chance, because `deleted` is a latch and the effect that navigates will not re-run |

Two details in the modal are worth keeping:

- **`discard()` resets the draft before releasing the navigation**, so the guard does not fire a second time against the edit the reader just agreed to lose.
- **`settleDiscard` is idempotent.** The modal's `(closed)` fires _after_ `discard` has already answered `true`, and letting that second call through would resolve the same promise with the opposite value. Escape therefore reads as "keep editing", which is defensible and is exactly what `(closed)` does.

## 7. Project files (US-905)

### 7.1 The story that added no upload code

Everything under `UploadStore` was already target-agnostic: `UploadTarget` is a discriminated `conversation | project` pair, and the transport, the staged poll, the four refusal shapes and the signed download all route through it. That was EP-8's whole reason for modelling a pair rather than a bare id — recorded at the time in [File Attachments §10](file-attachments.md#10-deliberately-not-here) as the story this was for.

The one change: `bindConversation` was generalised into a private `bindTarget`, with `bindProject` beside it.

```ts
function bindTarget(target: UploadTarget | null): void {
  const current = store.target();
  if (current?.kind === target?.kind && current?.id === target?.id) {
    return;
  }
  // …
}
```

Comparing `kind` as well as `id` is not pedantry — a conversation and a project are separate id spaces, so only the pair identifies a parent.

Two placement decisions on the detail screen matter more than they look:

- **`UploadStore` is provided on `ProjectDetail`, not on the files panel.** A file dropped on the composer and a file dropped on the Files tab are then the same set of chips, and switching tabs mid-upload does not tear the transfer down.
- **The target is bound the moment the id is known.** Unlike the chat composer's, this target is never absent — the screen only renders under a resolved `:projectId` — so a dropped file starts uploading immediately rather than waiting for a conversation to exist.

Because `blocksSend` is `target !== null && !isSettled()`, the project composer's Send also waits for project uploads to settle, with the composer's own "Preparing N files" note explaining the disabled control. That is the chat behaviour, unchanged, arriving for free.

### 7.2 A bare array, and an index-faithful removal

`GET api/projects/{id}/documents` answers with a **bare array** — no `PaginatedResponseDto`, no `skip`/`take`. So `ProjectDocumentsStore` holds a plain `readonly ProjectDocumentDto[]` rather than `withEntities`: the list is replaced wholesale on every read, there is no pagination to model, and an optimistic removal has to restore a row **at the index it came from**, which is bookkeeping an entity map would only get in the way of.

The route also emits both `id` and a read-only `documentId` alias of the same value. Only `id` is modelled — a second name for one value in store state is a bug waiting to be written.

Removal is optimistic with an index-faithful rollback, and the rollback is guarded:

```ts
// Only into the list it came out of. A refetch since then would have replaced the
// array, and splicing a stale row into it would resurrect a document the server has
// already removed.
if (store.projectId() === projectId) { … }
```

`mergeMap`, because removals of different documents are independent; same-id overlap cannot happen, since the row and its confirmation are gone and `remove` guards on the pending id.

### 7.3 A finished chip becomes a row by re-reading the list

The upload route answers with a **job id, never a document**, so the name, size and created date can only come from asking for the list again — cheap here precisely because it is unpaginated. Once the row exists the chip retires, or the same file sits on screen twice.

**That bridge lives on `ProjectDetail`, not on the files panel**, and this is the decision to preserve. `UploadStore` is provided on the detail screen so the composer can upload from any tab; an effect on the panel would only run while the panel is mounted. A file dropped on the composer while the reader is on Instructions has to become a row too, and the tab strip's own count would otherwise go stale.

### 7.4 The permission gating

Frame `4f`'s caption, and US-905's fourth criterion: without `Upload File` the drop zone and the add control are **absent**, and existing files stay listed and downloadable.

Two mechanisms, deliberately, exactly as US-204 requires: `@if` removes the visible affordance, and `FileDropTarget`'s own input is what makes drag-and-drop inert. An `@if` alone cannot express the second — a drop target that is not rendered is still a page the browser will happily navigate to the dropped file on.

Removing is an upload-permission action too, so the Remove button is inside the same `@if` while Download is not.

Download is unchanged from US-804 and carries its rules with it: the signed link is requested **at the click** and never on render, because it is a bearer credential with a five-minute life and a list that prefetched one per row would mint a credential for every file the reader merely looked at.

Focus after a confirmed removal falls to the browse control, then to the first surviving row's download button, then to `#main-content` — the one always-present programmatic target. Only when focus actually fell through.

## 8. Compose in a project (US-906)

### 8.1 The project screen does not stream

The criterion is that the turn streams **as it does on the chat route**, and the transcript, the activity timeline, the markdown pipeline and the reasoning panel all live there. Reproducing any of that on the project screen would be a second implementation of the app's most intricate path.

So the flow hands off:

```text
Send  →  POST api/conversations { projectId }
      →  ConversationListStore.prependNewest(created)   ← before navigating
      →  PendingPromptStore.hold(created.id, prompt)
      →  router.navigate(['/chat', created.id])
                     ↓
          TurnStore.bindRoute  →  claim(id)  →  the ordinary turn
```

The sidebar row is prepended **before** the navigation, so it is already there when the chat route renders — the same ordering US-401's own create uses.

Three consequences on the host, all deliberate:

- **`inFlight` is the create**, not a turn. It is what turns Send into Stop for the one round trip this screen owns.
- **`stop()` cancels that POST**, by unsubscribing before the `next` that would navigate. The POST has no abort surface of its own, so Stop means stopped rather than "carry on, but pretend otherwise". Nothing has to be unwound — the conversation was never created, so there is no row and no navigation.
- **`turnError` is always null.** The chat route puts a failed turn in the transcript with a Retry beside it; this screen has no transcript, so its failures are toasts — and a null keeps the composer's focus fixups behaving exactly as they do there.

`TurnStore` claims the prompt **after** its route reset, so the send lands on the binding it belongs to. If no model is selectable at that moment — the catalogue failed, or the reader landed before it resolved — the prompt is seeded into the composer rather than dropped, so the work is still theirs to send.

### 8.2 The prompt travels in a store, not the URL

The navigation destroys the composer holding the text, so the text has to survive it somewhere, and the router is **not** that somewhere: a prompt in a URL is a prompt in the browser's history, in the address bar, and in every referrer header the page sends.

`PendingPromptStore` is root-scoped and **single-shot**: `claim(conversationId)` clears in the same call, so there is no window in which a second reader could take the same prompt, a refresh does not resend, and a prompt left over from a navigation that never completed cannot attach itself to an unrelated conversation opened later — the id has to match.

### 8.3 Two failure shapes

| Failure                     | What happens                                                                                                                                   |
| --------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `resource-not-found` (404)  | "This project no longer exists" / "It was deleted, so the conversation could not be created." — and back to the grid, because this URL now names nothing. The server reports a foreign or deactivated project exactly as it reports one that never existed |
| Anything else               | The text goes **back to the composer** through `composerSeed` and the error is toasted — the same bargain `TurnStore` makes when a create is stopped |

## 9. Route, nav entry and bundle

`/projects` is a lazy `loadChildren` under the shell, and `loadChildren` rather than two sibling `loadComponent`s because both screens invoke the same dialogs and a shared parent is what lets those mount once. No guard of its own: the tree already sits behind `authGuard` and `sessionGuard`, and projects are scoped to their owner server-side, so there is nothing here a permission could gate.

The sidebar's **Projects** entry (`bi-folder2`) appears now that there is a route to point it at — the sidebar renders only entries whose route exists, and a link that redirects back to `/chat` is worse than no link ([Shell and Navigation §2.3](shell-and-navigation.md#23-adding-a-nav-entry)). Documents is still absent, and belongs to EP-10.

|                | Initial raw   | Initial transfer | Budget (warn / error) |
| -------------- | ------------- | ---------------- | --------------------- |
| After EP-8     | 663.92 kB     | 163.32 kB        | 670 kB / 720 kB       |
| **After EP-9** | **667.05 kB** | **168.02 kB**    | 670 kB / 720 kB       |

**No re-baseline.** The +3.1 kB is the `@ngrx/signals/events` handler API reaching `ConversationListStore`, which is on the initial graph because `SessionBootstrap` — and therefore `sessionGuard`, which is in the root route table — releases it. That store had never composed `withEventHandlers` before §5.5 needed it to. Everything else rides the lazy `projects` chunk or the composer chunk it now shares with `chat`; the styles bundle is unchanged at 63.96 kB against its 65/80 kB budget, and `check-initial-chunk.mjs` still reports 22 chunks reachable from `main.ts` with no markdown, math, diagram or admin code among them.

The prep step is bundle-neutral in both directions: moving the composer out of `features/chat/` did not put it in the initial graph, and the chat route did not grow — the composer stack simply moved into a chunk the two lazy routes share.

## 10. Testing

**84 specs across eleven files**, inside the suite's 1315.

| Area                           | Spec                       | Notable cases                                                                                                                                                                           |
| ------------------------------ | -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The grid's screen              | `projects` (9)             | **Exactly one request for a deep link that already carries a term**; the term written into the URL and nothing else driving the query; back restoring the unfiltered grid; both empty states with their own way out; the error panel replacing a half-drained set; and **no favourite star** |
| The drain                      | `project-list-store` (11)  | The first page at the take the server clamps to; `name=` when the URL carries one; **every page of a multi-page set drained**; the ceiling stopping it and saying so; an in-flight drain **superseded rather than raced**; created/renamed/deleted rows against the active search; and sign-out emptying it |
| The dialog                     | `project-form-dialog` (9)  | Empty for a create; seeded from the project on an edit, **instructions included**; **re-seeded when the project arrives a round trip later**; `maxlength` derived from the schema; the **board's** duplicate copy rather than the server's; and the inline message cleared on the next keystroke with no imperative reset |
| The lifecycle store            | `project-actions-store` (10) | The whole body posted and nothing applied until the server confirms; a duplicate rendered inline and **never as a toast**, degrading to one only when the dialog was force-closed; the created project handed back to its invoker; **the fetch before opening on a listing row**; the full representation on a rename; instructions saved by echoing the rest; the deletion announced only on the 204; and a reopen refused while a row's action is in flight |
| The pure helpers               | `project-helpers` (8)      | `toProjectBody` echoing every field, and **"leave it" told apart from "clear it"**; the duplicate message swapped and every other one verbatim; the PascalCase key the server actually sends |
| The detail screen              | `project-detail` (6)       | The tab strip's file count; **the upload store resolved through the outlet, so the panel shares one**; a finished upload becoming a row **while another tab is open**; leaving for the grid when the project is deleted under the reader; the guard **not trapping** them when it is; and the warning firing on a tab change |
| The composer host              | `project-composer-host` (7) | The conversation created inside the project and the prompt handed on; **`turnError` staying null**; a second press dropped rather than making two conversations; Stop cancelling and handing the prompt back; the 404 returning the reader to the grid; and a send refused before the route resolves |
| Standing instructions          | `instructions-panel` (7)   | Seeded from the only route that carries them; **settling only once the server confirms**; a clean panel left without a modal; a dirty one blocking until the reader decides; Escape read as keep-editing, **never as a silent discard**; and Cancel reverting in place |
| The files panel                | `project-files` (5)        | Rows with size and date; the drop zone, browse control and picker **with** the grant; **every add affordance withheld without it, files still downloadable**; the removal confirmed and the file named; and an attached file posted to the project's own upload route |
| The documents store            | `project-documents-store` (6) | **The bare array read with no envelope**; a row removed at once and **put back at its index** on failure; a second removal refused; refresh as the bridge from chip to row; and a previous project's response kept out of the new list |
| Relative time                  | `relative-time` (6)        | Minutes then hours inside the day; **"Yesterday" by the calendar, not by 24 hours**; the date fallback; a future timestamp read as "Just now"; and an unparseable value answering empty |

Plus one added to `conversation-list-store.spec.ts` — held rows released when their project is deleted — and the sidebar's nav-entry assertions updated in `shell.spec.ts`.

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 1315 specs across 109 files
npm run lint    # ESLint (now including the layering rule) + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs
npm run format  # Prettier
```

> **Verification status.** As with every epic before it, none of this has been exercised against a live API. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 11. Deliberately not here

All of these are recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table, so none quietly becomes permanent.

| Missing                                                            | Owner   | Notes                                                                                                                                                    |
| ------------------------------------------------------------------ | ------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Any sort control** on the grid, and frame `4d`'s ceiling wording | US-902  | No endpoint accepts a sort parameter. The drain exists so that story can be honest; until then the notice uses frame `4g`'s neutral sentence (§4.1)       |
| **The favourite star** on a card and in the detail header          | US-909  | `ProjectSummaryDto` carries no flag. Absent rather than inert — the call US-308 made about this star's conversation counterpart                          |
| **The Conversations tab**                                          | US-908  | Which also needs US-307 for its row menu, and US-907 to avoid draining conversation search to filter locally                                              |
| **A project picker in the composer**                               | US-307  | The project is fixed by the route today. `beginCreate(afterCreate)` already exists for it — that callback is the criterion about returning the user exactly where they were |
| **The dialogs living in `Shell`**                                  | US-307  | They mount on `ProjectsLayout` while every invoker is inside this feature. US-307 adds the first one outside it and should move them and pay the shell-chunk cost (§5.6) |
| **`SessionBootstrap` requesting projects** (US-202's fourth criterion) | US-307 | No root project list has a consumer yet. Both project lists here are route-scoped, and a root one with nothing reading it would be a request per sign-in for nothing |

## 12. Troubleshooting

| Symptom                                                                        | Cause                                                                                                                                                                        |
| ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **A project's standing instructions vanished after a rename**                  | A write body was built by hand instead of through `toProjectBody`. The PUT is a full representation and an omitted field is a cleared field (§5.2)                            |
| **Instructions vanished after editing a project from the grid's kebab**        | The form was opened on a `ProjectSummaryDto`. The listing omits `instructions` entirely, so the form seeded `''` and saved `null` — `beginEdit` must fetch first (§5.3)      |
| A conversation rename suddenly 404s, with nothing about projects in the request | Its project was deleted and the client kept the dead `projectId`. `toUpdateBody` echoes it; `releaseProject` and `ConversationStore`'s handler are what clear it (§5.5)      |
| The duplicate-name error reads "A project named 'X' already exists."           | `serverMessagesFor` was used directly instead of `projectNameMessages` (§5.4)                                                                                                |
| The inline name error will not clear as the user types                         | The `validate` rule stopped comparing against `formRejectedName`, or compares untrimmed. Signal Forms has no `setErrors`, so the rule *is* the clearing mechanism (§5.1)      |
| A build or lint failure naming `no-restricted-imports`                         | Something in `shared/`, `core/` or `domain/` imported from `@features/…`. Move the piece down a layer or invert it with a token (§3.4)                                        |
| The whole app downloads the chat chunk when opening `/projects`                | The same import, made before the rule existed. It type-checks and builds — the only symptom is the chunk graph (§3.4)                                                         |
| `NullInjectorError: No provider for InjectionToken COMPOSER_HOST`              | A screen rendered `<app-composer />` without `provideComposerHost(…)` on its injector (§2.1)                                                                                  |
| The project screen streams a turn in place, or shows half a transcript         | Something taught `ProjectComposerHost` to stream. It must create, hold the prompt and navigate — the transcript lives on the chat route (§8.1)                                |
| The prompt is sent twice, or a refresh re-sends it                             | `claim` stopped clearing in the same call, or the id check came off. It is single-shot **and** id-matched for exactly these two failures (§8.2)                               |
| A prompt appears in the address bar or in browser history                      | The handoff was moved to a query parameter. That also puts it in every referrer header (§8.2)                                                                                  |
| The grid shows a partial list with no notice                                   | `truncated` is being written in a `finalize`, so a cancelled drain looks the same as a truncated one — or the notice was moved inside a branch the drain does not reach (§4.1) |
| Rows from an old search term appear under a new one                            | `_querySuperseded$` was dropped. `switchMap` cancels one request; the drain is a chain of them (§4.1)                                                                          |
| Every card re-renders on each change-detection pass                            | `[link]` was bound to a fresh `[route, id]` array, or an "Updated …" label reads `Date.now()` from the template (§4.3)                                                        |
| A card's kebab item blurs to `<body>` when pressed                             | `aria-disabled` was replaced with the native `disabled` attribute (§4.3)                                                                                                      |
| The Files tab count reads 0 on a project that has files                        | The count was rendered before `isFulfilled()`. It is withheld until the list has actually been read (§6.1)                                                                    |
| Files uploaded from the composer never appear in the Files tab                 | The chip-to-row bridge was moved onto the files panel, where the effect only runs while that panel is mounted (§7.3)                                                          |
| A tab switch cancels an upload in progress                                     | `UploadStore` was provided on the files panel instead of on `ProjectDetail` (§7.1)                                                                                            |
| A removed file reappears at the top or bottom of the list                      | The optimistic rollback lost its captured index, or restored into a list a refetch had already replaced (§7.2)                                                                |
| The unsaved-instructions modal traps a reader on a deleted project             | The `deleted` short-circuit came off `confirmDiscard`. `deleted` is a latch and the navigating effect will not re-run (§6.3)                                                  |
| The discard modal asks twice, or discards after "Keep editing"                 | `settleDiscard` lost its idempotence — `(closed)` fires after `discard` has already answered (§6.3)                                                                            |
| Reopening `/projects` shows a dialog already open, holding an old attempt      | `ProjectsLayout`'s `onDestroy` cancel was removed. `Modal` closes on destroy without emitting `closed`, so root state is never told (§5.6)                                    |
| Two projects are created from one double-click                                 | `_create` was moved off `exhaustMap`. Their names then collide with each other (§5.1)                                                                                          |
| A second project delete is silently dropped                                    | `_delete` was moved onto `exhaustMap`. Deletes of different projects are independent (§5.1)                                                                                   |

## 13. Key files

| Concern                     | File                                                                                                                                                                                                                                                    |
| --------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Wire shapes and field caps  | [`domain/api/project.ts`](../../enterprise-gpt-ui/src/app/domain/api/project.ts)                                                                                                                                                                        |
| Date labels                 | [`domain/format/relative-time.ts`](../../enterprise-gpt-ui/src/app/domain/format/relative-time.ts)                                                                                                                                                       |
| Update body, duplicate name | [`core/projects/project-update.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-update.ts), [`duplicate-name.ts`](../../enterprise-gpt-ui/src/app/core/projects/duplicate-name.ts)                                                            |
| Lifecycle store             | [`core/projects/project-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-actions-store.ts)                                                                                                                                       |
| Events                      | [`core/events/project-events.ts`](../../enterprise-gpt-ui/src/app/core/events/project-events.ts)                                                                                                                                                         |
| Composer contract, pending prompt | [`core/chat/composer-host.ts`](../../enterprise-gpt-ui/src/app/core/chat/composer-host.ts), [`pending-prompt-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/pending-prompt-store.ts)                                                        |
| Shared history rule         | [`core/navigation/search-history.ts`](../../enterprise-gpt-ui/src/app/core/navigation/search-history.ts)                                                                                                                                                 |
| The composer                | [`shared/composer/composer.ts`](../../enterprise-gpt-ui/src/app/shared/composer/composer.ts)                                                                                                                                                             |
| Routes and layout           | [`features/projects/projects.routes.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.routes.ts), [`projects-layout.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects-layout.ts), [`detail/project-detail.routes.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-detail.routes.ts) |
| Grid                        | [`features/projects/projects.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.ts), [`project-list-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/project-list-store.ts)                                                    |
| Dialogs                     | [`features/projects/dialogs/project-form-dialog.ts`](../../enterprise-gpt-ui/src/app/features/projects/dialogs/project-form-dialog.ts), [`delete-project-dialog.ts`](../../enterprise-gpt-ui/src/app/features/projects/dialogs/delete-project-dialog.ts) |
| Detail screen               | [`features/projects/detail/project-detail.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-detail.ts), [`project-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-store.ts)                             |
| Instructions                | [`detail/instructions/instructions-panel.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/instructions/instructions-panel.ts), [`unsaved-instructions.guard.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/instructions/unsaved-instructions.guard.ts) |
| Files                       | [`detail/files/project-files.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/files/project-files.ts), [`project-documents-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/files/project-documents-store.ts)           |
| Compose in a project        | [`detail/project-composer-host.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-composer-host.ts)                                                                                                                                   |
| The layering rule           | [`eslint.config.mjs`](../../enterprise-gpt-ui/eslint.config.mjs)                                                                                                                                                                                         |
| The API side                | [`Enterprise.Gpt.Api/Endpoints/ProjectEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ProjectEndpoints.cs), [`ProjectService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ProjectService.cs)                               |
| Related reference           | [Projects (server)](../projects/project-management.md), [Conversation Library](conversation-library.md), [File Attachments](file-attachments.md), [Conversation Actions](conversation-actions.md), [Frontend Foundation](frontend-foundation.md), [Shell and Navigation](shell-and-navigation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
