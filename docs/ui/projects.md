# Projects

The `/projects` area: a grid that pages 25 cards at a time behind a Load more control, a lifecycle store whose one modal serves four entry points, standing instructions guarded against a navigation that would lose them, a files panel that added no upload code at all, a composer that deliberately refuses to stream, a searchable picker that puts a conversation into a project from anywhere in the app, and the project's own conversations panel — plus the structural move that made the composer possible, which is the largest thing EP-9 shipped and the least visible.

Audience: a developer putting the shared composer on a fourth surface, adding a control that has to name a project it only holds an id for, or debugging a project write that clears a field nobody edited. Read [Conversation Library](conversation-library.md) first for the list-screen conventions this grid copies — the URL contract, the route-scoped store, the empty states — [Conversation Actions](conversation-actions.md) for the move flow §9's picker invokes, [File Attachments](file-attachments.md) for the upload machinery §7 reuses wholesale, and [Frontend Foundation](frontend-foundation.md) for the composable store features and `core/errors` conventions everything here composes. Bare `§` references below are to sections of _this_ page.

The server side is the authority on everything past the request: [Projects](../projects/project-management.md), and [Document Upload and Ingestion](../documents/upload-workflow.md) for the pipeline a project file goes through.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Seven stories and one prep step, which is where the surprise is: the prep step is bigger than any of the stories. The last two closed EP-9 and, with it, the last of EP-3.

| Story                | What it delivers                                                                                                                                                                                             |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **prep**             | The composer stack moved to `shared/composer/` behind a `COMPOSER_HOST` token, `UploadStore` moved to `core/documents/`, and a `no-restricted-imports` rule so the layering that forced both is machine-checked |
| **US-901**           | Frame `4c`'s card grid, server-side `?name=` search on US-701's exact URL contract, and a **drain** — `take=100` to a 500-project ceiling — with the shortfall stated rather than silent                       |
| **US-903**           | Create, rename, describe and delete, from one root store and one modal; the client's own duplicate-name copy; a delete that releases the project's conversations across two other stores                       |
| **US-904**           | The project detail screen the rest of the epic hangs off — header, composer slot, tab strip — with tabs as **real child routes**, and standing instructions behind a `canDeactivate` warning                   |
| **US-905**           | A files panel that binds `{ kind: 'project', id }` and reuses EP-8's transport, staged poll, refusals and download unchanged                                                                                    |
| **US-906**           | A prompt typed on a project creating the conversation **inside** it — and then handing the turn to the chat route, because that is where the transcript is                                                     |
| **US-307**           | Frame `2e`'s searchable picker, a root `ProjectLookupStore` behind it, the composer's project pill, the project chip and kebab on the conversations library — and the dialogs' move into `Shell` (§9)          |
| **US-908**           | Frame `4g`'s Conversations tab: **one filtered request**, because US-907 landed with it, and an `updated` handler that is asymmetric on purpose (§10)                                                          |

Eleven decisions shape everything here, and each looks removable until you know what it prevents:

1. **The composer moved to `shared/` behind an injection token.** Frame `4e` puts the shared composer on the project screen, but it lived in `features/chat/` and injected the route-scoped `TurnStore`, and dependencies run one way. `Chat` provides `TurnStore` as the host; the project screen provides `ProjectComposerHost` (§3).
2. **The project screen does not stream.** US-906's criterion is that the turn streams _as it does on the chat route_ — and the transcript is there. So a send creates the conversation, holds the prompt in a root `PendingPromptStore`, and navigates (§8).
3. **The prompt travels in a store, not in the URL.** A prompt in the address bar is a prompt in the history and in every referrer header the page sends (§8.2).
4. **The grid pages, since the 2026-08-29 override.** It drained to a 500-project ceiling for most of its life, because a card grid that grows a page at a time keeps moving the reader's cursor. The product owner reversed that: the cursor cost is bounded, and the drain rendered up to 500 cards nobody had asked for. The order riding every appended page is the server's, not a comparator over what the client holds; `withClientQuery` is still not composed (§4.1).
5. **`toProjectBody` is the only place an update body is built.** `PUT api/projects/{id}` is a full representation: omitting `description` or `instructions` _clears_ them. The edit dialog and the instructions panel go through the same function (§5.2).
6. **`beginEdit` fetches before opening.** `ProjectSummaryDto` omits `instructions` **entirely** — absent, not null — so a form seeded from a grid card would PUT `instructions: null` over text the user never saw (§5.3).
7. **The duplicate-name message is the client's.** The server sends `A project named 'X' already exists.`; the board and the criterion require "You already have a project with this name." One message is swapped; every other `Name` failure renders verbatim (§5.4).
8. **A project delete releases its conversations, and the client must mirror that.** The server sets `ProjectId = null` on them, and `toUpdateBody` echoes `projectId` on every rename — so a client row still holding the dead id takes a 404 on its next rename, a failure with nothing to do with projects (§5.5).
9. **US-905 added no upload code.** `UploadTarget` was already a discriminated `conversation | project` pair, so the panel binds the project arm and reuses the lot. The one change was generalising `bindConversation` into a private `bindTarget` (§7.1).
10. **The picker is not a `Menu`.** `Menu` renders its own trigger, its panel is `role="menu"` — whose only valid children are `menuitem*` — and its keyboard handling swallows the arrows and closes on Tab. A search field inside that is three collisions, so the picker is a non-modal `role="dialog"` holding a listbox, borrowing only the placement and dismissal that genuinely are shared (§9.2).
11. **A root lookup keeps its pages when a drain fails; the grid throws its away.** They read the same endpoint and make opposite calls, because a chip that can still name 200 of 500 projects is better than one that names none, while a grid counted over a half-drained set — even one the server ordered correctly page by page — is a silently-partial list, which is the failure a mid-drain error replaces with the error panel instead (§9.1).

### 1.1 Where each piece lives

| Concern                                                              | Where                                                                                                                                                                                                                                            |
| -------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The wire shapes — `ProjectSummaryDto`, `ProjectDto`, `ProjectWriteBody`, `ProjectDocumentDto`, `PROJECT_FIELD_LENGTHS` | [`domain/api/project.ts`](../../enterprise-gpt-ui/src/app/domain/api/project.ts) — framework-free                                                                                          |
| "2h ago" / "Yesterday" / "Aug 5"                                     | [`domain/format/relative-time.ts`](../../enterprise-gpt-ui/src/app/domain/format/relative-time.ts) — framework-free                                                                                                                               |
| The one place an update body is built                                | [`core/projects/project-update.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-update.ts)                                                                                                                                              |
| The duplicate-name swap                                              | [`core/projects/duplicate-name.ts`](../../enterprise-gpt-ui/src/app/core/projects/duplicate-name.ts)                                                                                                                                              |
| The lookup's page size and ceiling, and the client's copy of the server's `LIKE` | [`core/projects/project-load.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-load.ts) — the constants now serve `ProjectLookupStore` alone; the grid has its own `PROJECT_LIST_PAGE_SIZE`                                                                     |
| Every project the user owns, held once at the root                   | [`core/projects/project-lookup-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-lookup-store.ts) — the picker's list, `nameOf(projectId)`, and since US-910 the `favorites`/`hasFavorites` computeds the sidebar reads (§9.1)      |
| Create, rename, describe, delete, favourite, and the dialogs' state  | [`core/projects/project-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-actions-store.ts) — root-scoped; `toggleFavorite` is US-909's addition (§5.7)                                                                    |
| The four project events, and every store that observes one           | [`core/events/project-events.ts`](../../enterprise-gpt-ui/src/app/core/events/project-events.ts) — `favorited` (US-909) is the newest (§5.7)                                                                                                       |
| What executes a prompt, as a token                                   | [`core/chat/composer-host.ts`](../../enterprise-gpt-ui/src/app/core/chat/composer-host.ts)                                                                                                                                                        |
| The prompt handed from one screen to another                         | [`core/chat/pending-prompt-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/pending-prompt-store.ts) — root-scoped, single-shot                                                                                                               |
| The history rule both search screens share                           | [`core/navigation/search-history.ts`](../../enterprise-gpt-ui/src/app/core/navigation/search-history.ts)                                                                                                                                          |
| The composer, model menu, tools menu, dictation                      | [`shared/composer/`](../../enterprise-gpt-ui/src/app/shared/composer/) — including US-307's project pill (§9.4)                                                                                                                                   |
| Frame `2e`'s picker                                                  | [`shared/projects/project-picker/`](../../enterprise-gpt-ui/src/app/shared/projects/project-picker/) — in `shared/`, injecting only `core/` stores, exactly as `Composer` does                                                                    |
| Where a fixed overlay panel sits, shared by `Menu` and the picker    | [`shared/overlay/anchored-panel.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/anchored-panel.ts)                                                                                                                                            |
| The upload transport's store                                         | [`core/documents/upload-store.ts`](../../enterprise-gpt-ui/src/app/core/documents/upload-store.ts) — [File Attachments](file-attachments.md)                                                                                                      |
| The area's route table and its layout                                | [`features/projects/projects.routes.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.routes.ts), [`projects-layout.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects-layout.ts)                                     |
| The grid and its paged list                                          | [`features/projects/projects.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.ts), [`project-list-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/project-list-store.ts)                                             |
| The two dialogs, mounted in the shell since US-307                   | [`features/shell/project-actions/`](../../enterprise-gpt-ui/src/app/features/shell/project-actions/) (§5.6)                                                                                                                                       |
| The detail screen, its route table and its stores                    | [`features/projects/detail/`](../../enterprise-gpt-ui/src/app/features/projects/detail/)                                                                                                                                                          |
| The Conversations tab panel                                          | [`features/projects/detail/conversations/`](../../enterprise-gpt-ui/src/app/features/projects/detail/conversations/) (§10)                                                                                                                        |
| Its store — moved to `core/` with US-910, since the sidebar needs it too | [`core/projects/project-conversations-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-conversations-store.ts) (§10.1)                                                                                                             |
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

`ComposerHost` is seven members — `composerSeed`, `inFlight`, `turnError`, `projectTarget`, `consumeComposerSeed()`, `send()`, `stop()` — deliberately not `TurnStore`'s whole API. A host that cannot stream reports `inFlight` for its own request, keeps `turnError` null, and has nothing to stop.

`projectTarget` is US-307's, and it is the one member with two arms rather than one value:

```ts
type ComposerProjectTarget =
  | { kind: 'conversation'; conversation: ConversationDto } // the pill opens the picker
  | { kind: 'project'; projectId: string }; // the pill is a static chip
```

Two arms because the two screens differ in kind, not in degree: on the chat route the project is a property of the open conversation and the reader may change it; on a project's own screen the project **is** the route and there is nothing to pick. A host with nothing to act on returns `null`, and the composer renders the control **absent** rather than disabled (§9.4).

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
- **Never reintroduce the drain to the projects grid, or a client-side filter under its paging.** A filter over the loaded window would search 25 of 1,284 cards and report the answer as the whole one (§4.1).
- **Never render a project id when `nameOf` answers null.** It means the project is unknown or past the drain ceiling; the chip and the pill both render nothing there, because a guid is not a name and "No project" would be a lie (§9.1).
- **Never compose `withClientQuery` on `ProjectLookupStore` for the picker's search term.** Three pickers can be open across the app at once, and a term in root state would narrow all of them from whichever one is being typed in (§9.2).

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

### 4.1 It pages, and it used to drain

```ts
export const PROJECT_LIST_PAGE_SIZE = 25; // this grid's own, matching LIBRARY_PAGE_SIZE
```

Its own constant rather than `core/projects/project-load.ts`'s `PROJECT_PAGE_SIZE`: that one is chosen so `ProjectLookupStore`'s drain costs the fewest round trips, and this one is chosen for a grid with a Load more control beneath it. `project-load.ts` still owns `PROJECT_PAGE_SIZE`, `PROJECT_LOAD_CEILING` and `matchesProjectTerm` — the client's copy of the server's case-insensitive `LIKE '%term%'` — because the lookup store needs the first two and both stores need the third, and `core` may not import `features`.

**This grid drained to a 500-project ceiling until 2026-08-29**, and the store's own comment said a Load more would never exist here: a card grid that grows a page at a time keeps moving the reader's cursor. The product owner reversed that decision. The cursor cost is real but bounded; the drain rendered up to 500 cards nobody had asked for, over five requests fired on mount, and hid every project past the ceiling behind a sentence with no way out of it. The store keeps the reasoning as a dated record rather than deleting it, because it was sound at the time and is worth reading before anyone proposes the drain again.

What replaces it is exactly what the conversations library ships (`ConversationLibraryStore`), including the reasons:

- **`switchMap` on the query, `exhaustMap` on the page.** The first cancels a superseded search; the second drops a double press, which would otherwise fetch the same window twice because `skip` has not moved yet.
- **`_querySuperseded$` cancels a page a later query overtook.** `switchMap` cancels one *query* for the next, but a page is a separate request on a separate `rxMethod`: without the subject, a page fetched against the old term lands after the new query's `setAllEntities` and appends a window from a grid that is gone. `_dropCard` fires it too — a page on the wire carries the pre-removal offset, so letting it land would skip the card that slid into the gap.
- **`loadMore()` guards on all three of `!isPending() && !loadingMore() && hasMore()`.** `isPending()` is the load-bearing half: `_querySuperseded$` cannot help a page started *after* a query was already in flight.
- **A failed page keeps its cards and toasts; a failed first page replaces the grid.** The cards on screen belong to a query that succeeded, so swapping the grid for an error panel would throw away a usable result set. A first-page failure has no such set behind it.
- **`sort=`/`dir=` ride every appended page**, which is what makes a Load more page a continuation of the same run rather than a second sorted run underneath the first — the failure a control here was refused over until US-706 made the order the server's. The select above the grid is correspondingly **never disabled** (§4.3).

The control itself is bespoke markup below `.projects__grid` rather than a projected `tableFooter` slot, because this screen renders no `<app-data-table>`. It is removed — not disabled — once `hasMore()` is false, carries `aria-disabled` while a page is in flight, and hands focus to the last card's name link when it removes itself, falling back to the search field.

**`withClientQuery` is not composed, and that is a settled decision rather than a deferral.** The store's own doc block used to forecast composing it here once a sort control shipped, over `entities` with `isAuthoritative: isFulfilled() && isFullyLoaded()` — written when no endpoint accepted an order at all. US-706 changed that premise, and the search here was already server-side, so composing the feature now would add a second filter over an already-filtered set and a comparator nothing would call.

### 4.2 The URL contract, shared with the conversations library

Identical to [Conversation Library §3](conversation-library.md#3-the-url-is-the-source-of-truth), down to the function: `?name=` is bound by `withComponentInputBinding()` and drives the store; `onSearched` is the only writer and it writes to the router; `[value]` is bound one-way; nothing navigates from an effect over `name()`.

`shouldReplaceHistory` moved to `core/navigation/search-history.ts` when this screen needed it — `features → features` is not a direction this app has, and a second copy is a second thing to get wrong. `features/conversations/conversations-route.ts` now re-exports it so that screen's route contract still reads from one file.

**A second key, `?sort=`, joined it with US-902.** It names one of four orders (`updated | created | name | favorites`) rather than the API's two-parameter `sort=`/`dir=` pair — the URL is a contract with the reader, not a proxy for the request underneath it, the same reasoning that spells the URL's page size `size` where the API says `take` (§2.2 of [Administration](administration.md#22-the-three-url-parameters)). `toProjectSort` restricts a hand-edited value to the offered set rather than passing it through, so the select can never display an order the URL contradicts and the server never sees a value it would answer with a 400 nobody asked for. `sortKey` holds `''` for the default order rather than spelling out its name, which is what lets `shouldReplaceHistory` read a sort change on the same terms as a search term: turning an order on or off pushes, moving between two non-default orders replaces — because a closed `<select>` fires `change` on every arrow key, and three pushes for one gesture would bury the unsorted grid under entries nobody chose.

The grid's own route file keeps the parameter names:

```ts
export const NAME_PARAM = 'name';
export const SORT_PARAM = 'sort';
export { shouldReplaceHistory } from '@core/navigation/search-history';
```

### 4.3 The screen

- **`ProjectCard` leads with the brand-tinted `bi-folder-fill` glyph**, grouped with the name in a new `.project-card__identity` wrapper rather than added as a third child of the head — that keeps the head's `space-between` pushing the actions to the far edge, and keeps the link's hit area exactly the name. It had been the one place in the app that named a project without it: the sidebar row, the picker, the composer's project pill, the library's project chip and this same screen's detail header all lead with the glyph, frame `4c` draws it, and US-901's acceptance criteria name it. `bi-folder-fill` was already in the icon manifest and the sprite, so nothing needed rebuilding. The name now ellipsises rather than wrapping, for the same reason `project-card__glyph` is `flex: 0 0 auto` — text is what has to give when the card narrows, not the glyph.
- **Two empty states, not three.** `hasNoMatches()` gets frame `4b`'s ridgeline treatment with the term named and a **Clear search** action; `hasNoProjects()` gets "No projects yet" and a **New project** action. There is no favourites filter here to need a third.
- **The heading reads `projects.name()`, never the component's `name()`.** The store's copy is the one the rendered cards belong to, so the heading cannot name a term whose results have not arrived.
- **"Updated …" labels are computed once per result set**, from a `Date.now()` sampled inside a `computed`. A clock read from a template binding is a fresh value on every change-detection pass, which `provideCheckNoChangesConfig({ exhaustive: true })` compares between passes — and would recompute every label on every unrelated render. Labels are minute-granular at their finest, so nothing visible is lost.
- **The card's link is a string, not a commands array.** `ProjectCard.link` is a signal `input()`, so a fresh `[route, id]` literal fails `Object.is` every pass, writing the input, marking every card dirty and re-running `RouterLink`'s href update — OnPush defeated across the whole grid. It is also **absolute**, because this component's own route is a path-less child and a relative `[project.id]` would resolve against whichever route happened to host the card.
- **The kebab items are `aria-disabled`, never natively `disabled`.** The native attribute blurs the item the instant it is pressed, dropping a keyboard reader onto `<body>` for the whole round trip. `beginEdit` and `beginDelete` both refuse on the pending id, so the guard is real either way.
- **The star is live**, since US-909 put `isFavorite` on `ProjectSummaryDto` — `[favorite]="project.isFavorite"` and `(favoriteToggled)="actions.toggleFavorite(project)"`, confirm-then-render like every other card write rather than optimistic: `pending` covers the round trip, and `ProjectActionsStore` has no channel into this route-scoped store to roll a guess back through if the server disagrees (§5.7). **The sort select is live too, since US-902** — four options (Last updated, Recently created, Alphabetical, Favourites first), bound on the options rather than as `[value]` on the `<select>` itself, because that binding runs before `@for` inserts them and would set `selectedIndex = -1` on an empty list, which the browser's own reset then resolves to the first option regardless of what the URL asked for. It is never disabled, over a partially loaded grid or a complete one (§13).
- **Focus is repaired when the last card leaves.** `beginDelete` arms the fixup at the gesture rather than inferring it from the grid emptying — the removal is confirmed elsewhere, so by the time the card goes there is nothing left to tell the screen the reader caused it. The search field is the target, because it is the only control outside every branch of the template. Only when focus actually fell through.
- **The grid became a grid in US-1403, having never been one.** Frame `4c`'s three-up layout was written with `row g-3` and `col-12 col-md-6 col-xl-4`, and **none of those classes exists**: `_bootstrap-components.scss` deliberately omits `bootstrap/scss/grid` at 10.8 kB, with a comment promising an intrinsic replacement. The screen had therefore been a plain block stack at every width since it shipped — on a laptop as much as on a phone. It is now `repeat(auto-fill, minmax(min(280px, 100%), 1fr))`, and the `min()` is not decoration: a hard 280px track minimum overflows its container below roughly a 308px viewport, which is the horizontal scrollbar US-1403's last criterion forbids.
- **Both project screens now scroll themselves.** `.shell__main` is `overflow: hidden`, so a routed host with no scroll container is _clipped_ below the fold rather than scrolled; the grid and the detail screen each take `overflow-y: auto` plus the `min-height: 0` a flex child needs before it will shrink below its content ([Responsive layout §9.2](responsive-layout.md#92-four-routed-screens-clipped-below-the-fold)).

## 5. Lifecycle (US-903)

### 5.1 One root store, one modal, four entry points

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

`beginCreate(afterCreate?)` takes a callback rather than raising an event, because US-903's third criterion is that the composer's invoker returns the user _exactly where they were_ with the new project selected. That answer belongs to one caller; `projectEvents.created` is already carrying the fact to everyone else. It fires **after** the dispatch, so a caller that navigates finds the lists already holding the row it is navigating to. **US-307 is that caller**, and the continuation had been sitting unused since US-903 waiting for it: the picker's pinned "New project" creates the project and moves the conversation into it in one gesture (§9.3).

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

When this was written nothing rendered `projectId` at all, which is what made the defect worth stating in advance. US-307 changed that in both directions: the library's project chip and the composer's pill now **show** a stale id as a name that stops resolving, and the move flow is a second `toUpdateBody` caller that would echo the dead one. `releaseProject` is what keeps both honest.

That third-feature reach is also what puts this on the event bus rather than on a method call. `ProjectActionsStore` is root-scoped and knows nothing about conversations; `ProjectListStore` and `ProjectStore` are scoped to their routes. Four observers across three scopes is exactly the case a method call cannot serve.

Removal is **not** optimistic here, unlike a conversation delete: the same event releases conversations across two stores, and undoing _that_ on a failure would mean re-guessing which rows the server had reached.

### 5.6 Where the dialogs mount

`ProjectFormDialog` and `DeleteProjectDialog` mount **once**, driven by the root store, so every invoker reaches the same instance. A native `<dialog>` renders in the top layer, so their position in the template carries no visual meaning.

**They moved from `ProjectsLayout` to `Shell` with US-307**, which is the story that was always going to do it. They sat on the layout while every invoker — the grid's cards, a project's own header — was inside this feature; the picker's "New project" is the first one outside it, and it opens from the sidebar, the chat header and the composer, none of which the projects layout renders. `ProjectsLayout` is now a bare `<router-outlet />` that exists only because both screens hang off one lazy `loadChildren`.

The move also **retired a piece of housekeeping rather than relocating it.** The layout used to cancel both dialogs on destroy: the store is root-scoped and the dialogs were not, `Modal` closes its native element on destroy **without** emitting `closed`, so a reader who left `/projects` with the create dialog open would leave `formMode` set in root state and find it re-mounted already open on the way back. `Shell` is not unmounted while signed in, so root form state can no longer be stranded by a navigation, and the `DestroyRef` block went with the dialogs.

One consequence is **accepted rather than overlooked**: a project dialog dismissed with the browser's Back button now stays open over the next route, because nothing unmounts it. That is exactly how the conversation dialogs have always behaved, so US-307 makes the two consistent instead of leaving one of them special. A route-independent cancel belongs to whichever story decides to fix both.

### 5.7 Favourites (US-909), and why the flip is not optimistic

`ProjectActionsStore.toggleFavorite(target)` is the whole call site: read the current flag off whatever copy the caller last saw, send its opposite, and let the pending-id guard **be** the concurrency control rather than adding one. It opens nothing that a second click could collide with — the request is a `PUT` of a fixed state, not a create or a delete — so `toggleFavorite` checks `pendingIds()` for the project's id before it does anything, and a double-click's second call finds the id the first one already set and no-ops. That works only because `setPending` runs synchronously inside the `rxMethod` projection, in the same call the first click made.

```ts
toggleFavorite(target: ProjectSummaryDto): void {
  if (store.pendingIds().has(target.id)) {
    return;
  }
  _favorite({ target, isFavorite: !target.isFavorite });
}
```

`_favorite` runs on `mergeMap` — favourites of different projects are independent and may overlap — with same-id overlap already closed by the guard above, so nothing else opens in front of it the way `exhaustMap` does for create/update.

**The flip is deliberately not optimistic**, unlike the conversation star (`ConversationListStore.toggleFavorite` patches its own row before the response). `ProjectActionsStore` is root-scoped in `core/` and has no method channel into `ProjectListStore` or `ProjectLookupStore`, which live in `features/` and `core/` respectively but are different stores it cannot reach into. The only pre-flight event on offer would be `projectEvents.updated` carrying a synthesized `ProjectDto` — and every store that observes `updated` adopts the DTO **whole**, including `ProjectStore` on the detail screen, which would overwrite `instructions` with a value nothing fetched and blank the standing instructions US-904 exists to protect. So the round trip gets a fourth, narrower event instead:

```ts
favorited: type<ProjectFavoriteChange>(); // { id, isFavorite } — the 204 has no body to adopt
```

`ProjectListStore`, `ProjectStore` and `ProjectLookupStore` each patch **only** `isFavorite` in response — never a whole-row adoption — which is also why none of them evicts a row for it: a star does not change `?name=`, and the server does not bump `dateModified` for it either (§3.5's server reference has both), so there is no ordering to restore and nothing else to reconcile. `pending` is what covers the round trip on screen instead of an optimistic guess.

Both stars that shipped with this story read and write through the same call: the grid card's (§4.3) and the project detail header's, frame `4e`'s deferred one. The header's is a plain button beside the editable name, styled one size down from the card's — `[attr.aria-disabled]`, no `aria-pressed`, because `favoriteLabel()` already renders "Favourite Helios" or "Unfavourite Helios" and a control that both names its action and reports a pressed state announces the direction twice. `ProjectCard`'s star made the same switch away from a native `[disabled]` in the same batch: `setPending` marks the id synchronously with the click, and a native `disabled` blurs the button the instant it is pressed, dropping a keyboard reader onto `<body>` for the whole round trip.

## 6. The detail screen and standing instructions (US-904)

Frame `4e`, in the board's own order, which is load-bearing: **header, then composer, then tabs.** Sending from the composer creates a conversation inside the project, so it belongs to the project rather than to whichever panel happens to be open.

### 6.1 Tabs are real child routes

```text
/projects
 └─ ''            ProjectsLayout           ← a bare outlet since US-307 (§5.6)
      ├─ ''       Projects                 ← the grid
      └─ :projectId   providers: [ProjectStore, ProjectDocumentsStore, ProjectConversationsStore]
           └─ ''  ProjectDetail
                ├─ ''             → redirect to 'instructions'
                ├─ 'instructions' canDeactivate: [unsavedInstructionsGuard]
                ├─ 'files'
                └─ 'conversations'          ← US-908
```

Two things follow from real routes that a signal holding a tab name cannot give: the panel a reader is on **survives a refresh and can be linked to**, and **`canDeactivate` has something to attach to** — which is how the unsaved-instructions warning fires on a move to the Files tab as well as on a move out of the project.

**Every store is provided on the route, not on its panel.** The parent and every tab share one instance of each, `ProjectStore` is torn down when the reader leaves the project rather than when one panel unmounts, and the two counts frame `4e` puts on the tab strip stay readable from the Instructions tab, where neither panel exists. `ProjectConversationsStore` joined the other two for exactly that reason and no other (§10).

A count is `null` until its list has actually been read. "Files 0" on a project that has four is a worse answer than no count, and the strip renders before the panels it counts.

**Below 768px the strip becomes `PillSubnav`, and that is a departure from frame `4e` rather than a rendering of it.** The frame's caption asks for stacked accordions; the tabs become the same scrollable pill strip the administration area uses, with the same links, the same routes and the same `aria-current="page"`. The departure was agreed with the product owner before implementation, and the reason is the section above: these are **real child routes**, one of them behind a `canDeactivate` guard that can _refuse_ the navigation. A disclosure marked `aria-expanded` would announce a state the control neither owns nor can guarantee — the reader presses it, the guard opens a modal, the navigation is cancelled, and the control has already said it is expanded. `aria-current="page"` on a link states the same thing honestly, and reusing the strip leaves the application with one responsive navigation pattern instead of two.

`PillItem` gained an optional `count` to carry it, rendered as the board's smaller run beside the label and withheld while `null` — the same rule the wide strip already followed. See [Responsive layout §8.5](responsive-layout.md#85-the-one-departure-from-the-boards).

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

## 9. The project picker (US-307)

Frame `2e`: a 320 px panel with a search field, the reader's projects as a single-select list, "Remove from project", and a pinned "New project". It is **one control with five invokers** — the board says so itself, in a note on the frame reading "same control as the conversation header's Move to project" — and they differ only in what the panel anchors to. The move it performs belongs to [Conversation Actions §7](conversation-actions.md#7-move-into-or-out-of-a-project-us-307); this section is the panel and the data behind it.

It lives in `shared/projects/` and injects only `core/` stores, exactly as `Composer` does and for the same reason: `features/chat`, `features/conversations`, `features/shell` and `features/projects` all render it, and none of them may import another.

### 9.1 The lookup behind it, and why a failed drain keeps its pages

`ProjectLookupStore` (`core/projects/`) holds every project the signed-in user owns, once, at the root. It is the root project list **US-202's fourth criterion asked for and nothing had a use for until now** — `SessionBootstrap` releases it beside the model catalogue, the MCP catalogue and the conversation list, which is what closes the last interim behaviour on that row.

It serves two things, and both need the *whole* set rather than a page:

- **The picker's list**, filtered client-side as the reader types. A server round trip per keystroke is the wrong shape for a 320 px popover, and a page already in hand is not a stable window to filter against a server-side term either.
- **`nameOf(projectId)`**, which is the only way any project chip can be rendered at all: `ConversationDto` carries the id and no name. This mirrors exactly how the conversations library resolves a `modelId` against `ModelCatalogStore`, down to rendering nothing rather than a guess when the id is unknown.

It drains the way the grid does (§4.1) and keeps three of the same load-bearing details — `expand` rather than a self-referential `concat`, `_querySuperseded$` cancelling the whole chain, and `truncated` written where the drain stops and never in a `finalize`. What it does not have is a term: this list is never filtered on the server, so a page can only be superseded by a retry or a sign-out.

**It differs from the grid on one thing, and the difference is the decision worth keeping.** A failure mid-drain leaves the grid showing an error panel instead of its cards, because a half-drained set sorted or counted as if it were whole is a silently-partial list. Here the pages already loaded are **kept** alongside the error: `setError` writes only `requestStatus`, `nameOf` keeps answering for the rows it does hold, and a chip that can still name 200 of 500 projects is strictly better than one that names none. The picker is what renders the error, and it hides the list behind it.

**Since US-706, every page of this drain asks for `sort=favorite&dir=desc`** rather than the server's default order — starred projects first, alphabetical within each group. That is what makes `favorites` — and so the sidebar's whole **Favourite projects** section ([Shell and Navigation §5.7](shell-and-navigation.md#57-favourite-projects-in-the-sidebar-us-910)) — complete rather than best-effort: a starred project sitting past the 500-row ceiling would previously never appear, and this order puts every favourite ahead of the cut. `projects` (every loaded row, in this order) is held rather than re-derived on each write, so a project starred or created since the last drain is patched in place instead of moved, and settles into its true position on the next drain.

Three `projectEvents` handlers keep it honest — server-confirmed facts only, which is that group's whole contract. Without them the picker would offer a project that has been deleted, or omit the one the reader just created from the panel they created it in. A creation goes to the **head** as a **guess**, not a fact the server confirmed: with the drain asking for `sort=favorite&dir=desc`, a refetch would actually file a brand-new, non-favourite project alphabetically inside the second group rather than at the top — the same guess the grid makes under a non-default order (§4.3), and for the same reason: the reader just created this project and every surface reading this store should be able to find it without waiting on a re-drain. A rename never evicts a row here, unlike in the grid, because no term filters this list. A favourite toggle patches `isFavorite` in place and does **not** re-sort the row, even though favourite is now the order's primary key — re-draining up to five pages because one star was pressed would rearrange the list under whatever was reading it at the time; `favorites()` filters rather than slices, so it stays correct regardless, and only the row's position among non-favourites waits for the next drain.

Being root-scoped puts it on the initial bundle graph, which is the same cost `ConversationListStore` already pays for the same reason (§11).

### 9.2 Not a `Menu`, and what it borrows from one

Every other panel of this shape in the app is a `Menu`. This one cannot be, and the reasons are structural rather than stylistic — a search field inside a menu is three separate collisions:

| `Menu` does | Which breaks |
| --- | --- |
| Renders its own trigger | The composer pill and four kebab items are the triggers, and one of them is not even in the same component |
| Marks its panel `role="menu"` | `menu` owns `menuitem*` and nothing else, so a text input in it is an `aria-required-children` violation |
| Owns the arrow keys and closes on Tab | The arrows have to move focus **from** the field into the rows, and Tab has to leave |

So the picker is a non-modal `role="dialog"` — `aria-modal` deliberately absent, because the page behind it stays live exactly as it does behind a kebab — holding a `role="listbox"` of `role="option"` rows. What it borrows from `Menu` is the part that genuinely is shared: `onDismiss` for outside-press and scroll, and placement, which was extracted into `shared/overlay/anchored-panel.ts` and is now used by both. Two details in `anchoredPosition` are easy to lose and are commented where they live: an `up` panel anchors by its **bottom** edge, because these panels swap content while open and a top-anchored one would grow *downward* over the trigger it is supposed to sit above; and the viewport height is `window.innerHeight`, not `documentElement.clientHeight`, which differ by the horizontal scrollbar when one exists.

**The search field is the panel's only tab stop.** Every row carries `tabindex="-1"` and is reached with the arrows, because the drain ceiling is 500 projects and an empty term matches all of them — 500 tab stops inside one popover is not a navigable control. Tab therefore leaves the panel and closes it on the way out, which is what keeps a 320 px overlay from floating over a page whose Escape handler it no longer owns. Home and End move to the ends of the list **only from a row**: inside the field, Home means "start of the text".

The term is a plain signal on the component, not `withClientQuery` on the lookup store. Three pickers can exist at once — a sidebar row, the chat header, the composer — and a term shared through root state would narrow all of them from whichever one is being typed in; it is also transient UI state that must not survive the panel closing.

The filter itself is `matchesProjectTerm`, the same function the grid uses to decide whether a created or renamed project still belongs to its active search, so a typed term narrows the panel exactly as `?name=` would narrow a request.

### 9.3 What the picker says that the board does not

Two departures from frame `2e`, both deliberate:

- **"Remove from project" is absent for a standalone conversation**, where the board draws it unconditionally. That is the repo's rule for an action with nothing to act on, and the same call US-901 made about the project card's star.
- **The drain ceiling is stated.** The board draws no such line, but a list that silently omits projects is the failure the grid's own ceiling notice exists to prevent — and here it would silently omit the project the reader is looking for. The wording is the lookup store's own `ceilingNotice`, counting what is loaded against what the server says exists.

The pinned **New project** action is `beginCreate`'s continuation, which has existed unused since US-903 for exactly this: the picker opens the shared create modal and moves the conversation into whatever it creates. The panel closes first — the dialog is where the reader is now, and a popover left open behind a modal is a second dismissal target.

### 9.4 The composer's project pill

The pill sits between the paperclip and the model pill, and it is the reason `ComposerHost` grew a seventh member (§2.1). Its two arms come straight off `projectTarget`: on the chat route it is a button that opens the picker upward; on a project's own screen it is a **static chip**, because a conversation created there is created inside that project by definition and there is nothing yet to move.

`TurnStore` resolves the target with **the list copy first and the detail DTO behind it** — the same precedence `ConversationStore.isFavorite` uses, and the reverse of its `current`. The list row is the one `moveRow` patches optimistically, so the pill flips on the click rather than a round trip later; the detail DTO is the fallback for a conversation the 50-row sidebar never held, which is every deep link past its first page and every row opened from the library. Reading only the list would leave the pill permanently absent there, while the header kebab beside it offered the move.

Three smaller calls on the control:

- **Absent on empty `/chat`**, and for the one round trip a deep link spends with neither copy in hand — the rule the header kebab already follows, because acting on a guess would move the conversation somewhere nobody asked for. A conversation that does not exist yet has nothing to move, and giving the empty state a project target is a deliberate scope boundary rather than an oversight.
- **Natively `disabled` while a turn runs**, like the attach control beside it and unlike the microphone, whose Stop must stay reachable. `composer__aux` already dims it to 40 %, and a control that looks unavailable and still opens a popover reads as broken. The native attribute is safe here because the pill sits outside any roving-focus panel.
- **It is the picker's own toggle**, which is the case `onDismiss`'s `insideAlso` handler and the picker's `anchorToggles` input exist for ([Conversation Actions §7.3](conversation-actions.md#73-handing-a-kebab-off-to-the-picker)).

## 10. A project's conversations (US-908)

Frame `4g`, on the detail screen's third tab: name, relative updated time, star, kebab. Four columns and **none of the library's toolbar** — no search field, no favourites filter, no selection — because the board draws none of them, and the row actions are US-307's five routed through `ConversationActionsStore` exactly as the sidebar row and the library table are.

### 10.1 One filtered request, and the criterion that never shipped

US-908's first acceptance criterion describes an interim behaviour: drain conversation search at `take=100` to a 500-item ceiling, filter by `projectId` on the client, and state "Showing the 500 most recent conversations" when the ceiling is hit. That criterion applied **only while US-907 was outstanding**, and US-907 landed with this story — so the panel ships the second criterion instead, and the interim behaviour never existed in any build.

What it ships is one request per query: `GET api/conversations/search?projectId=…&skip=&take=25`. There is no drain, no client-side filter and no ceiling notice, and a spec pins the absence of the notice so nobody reintroduces it from the criterion.

`ProjectConversationsStore` copies `ConversationLibraryStore` minus the search field and the selection, and keeps the parts that are about correctness rather than about the toolbar: the query/page split (`isPending` replaces rows, `loadingMore` appends to them), `exhaustMap` on the page so a double-click is not a request for the page after it, `_querySuperseded$` on a page whose project the reader has since left, `takeUntil(signedOut$)` on the request because [`withResetOnSignOut` clears state and does not cancel work](frontend-foundation.md#55-withresetonsignout--compose-it-last), and that feature composed last. A failed **page** raises a toast and keeps its rows; a failed **query** replaces the table with the error panel, because those rows belong to a query that failed.

**The store is provided on the detail route, not on the panel** — the same placement, for the same reason, as `ProjectDocumentsStore` (§6.1): the tab strip's count has to be readable from the Instructions tab, where this panel does not exist.

### 10.2 The asymmetric `updated` handler

The panel's `conversationEvents.updated` handler does two different things depending on whether it already holds the row, and **both halves matter**:

| The event says | The store holds the row | The store does not |
| --- | --- | --- |
| `projectId` still matches | Patch it in place — a rename or a favourite from anywhere | Ignore it |
| `projectId` no longer matches | **Drop** it and shift the counters | Ignore it |
| `projectId` now matches | — | **Re-read the query** |

The drop is what makes a move performed from **this panel's own kebab** remove the row it was invoked from, and it is US-703's rule for an unstarred conversation under the favourites filter applied one predicate over.

The re-read is the half that is easy to leave out. The sidebar row kebab opens the same picker while this screen is up, so a conversation can be moved *into* the project whose screen the reader is standing on, with both surfaces visible. This store's whole predicate is `projectId` and the payload carries it, so — unlike the library, where a rename cannot be known to match a `name=` filter without asking — the membership question is answerable here. It re-reads rather than inserting because the row's slot in `dateCreated desc` is the server's to decide; `setPending` leaves the loaded rows on screen, so the panel does not blank while it happens.

### 10.3 The panel

- **Remove from project is unconditional here**, unlike every other kebab that offers it. Every row on this panel is in a project by construction, so the action always has something to act on.
- **The star is confirm-then-render**, as on the library and for the same reason: this store owns its own entities, `ConversationActionsStore` cannot patch them, and an optimistic flip would have no rollback behind it.
- **The empty state projects no action.** Frame `4g`'s caption points at the project composer, which is already on screen and *earlier in reading order* — a button here would be a second way to reach something one Shift+Tab away rather than the only way. It is a level-2 heading, because this screen's header renders the project name as a button rather than a heading.
- **Focus is repaired twice over.** Load more removes itself when the last page lands, and Remove-from-project or a completed move evicts the row whose kebab the reader is standing in — optimistically, before the server confirms. Both fixups are armed at the gesture rather than inferred from the list shrinking, and the eviction one disarms itself when the row is still present and no request is pending, so it cannot fire against an unrelated later removal. Focus falls to the first surviving row, then to `#main-content`, because this panel — unlike the library, which has a search field — has no control outside every branch of its own template. Only ever when focus actually fell through.

## 11. Route, nav entry and bundle

`/projects` is a lazy `loadChildren` under the shell, and `loadChildren` rather than two sibling `loadComponent`s because it is the shared route both screens hang off. No guard of its own: the tree already sits behind `authGuard` and `sessionGuard`, and projects are scoped to their owner server-side, so there is nothing here a permission could gate.

The sidebar's **Projects** entry (`bi-folder2`) appears now that there is a route to point it at — the sidebar renders only entries whose route exists, and a link that redirects back to `/chat` is worse than no link ([Shell and Navigation §2.3](shell-and-navigation.md#23-adding-a-nav-entry)). Documents arrived the same way with EP-10's US-1002 ([Documents Library](documents-library.md)).

|                          | Initial raw   | Initial transfer | Budget (warn / error) |
| ------------------------ | ------------- | ---------------- | --------------------- |
| After EP-8               | 663.92 kB     | 163.32 kB        | 670 kB / 720 kB       |
| After EP-9               | 667.05 kB     | 168.02 kB        | 670 kB / 720 kB       |
| **After US-307/US-908**  | **668.90 kB** | **168.23 kB**    | 670 kB / 720 kB       |

**No re-baseline, and 1.1 kB of headroom left.** EP-9's +3.1 kB was the `@ngrx/signals/events` handler API reaching `ConversationListStore`, which is on the initial graph because `SessionBootstrap` — and therefore `sessionGuard`, which is in the root route table — releases it. US-307's +1.85 kB is the same shape: `ProjectLookupStore` is root-scoped and released from the same place, so it is on that graph too. The picker, the project dialogs and every chip they feed ride lazy chunks. The styles bundle is unchanged at 63.96 kB against its 65/80 kB budget, and `check-initial-chunk.mjs` still reports no markdown, math, diagram or admin code reachable from `main.ts`.

**The next root-scoped store trips the warning line**, which is now the number to know before adding one: ask whether the state genuinely has to outlive a route before providing it at the root.

The prep step is bundle-neutral in both directions: moving the composer out of `features/chat/` did not put it in the initial graph, and the chat route did not grow — the composer stack simply moved into a chunk the two lazy routes share.

## 12. Testing

**133 specs across fifteen files**, inside the suite's 1385.

| Area                           | Spec                       | Notable cases                                                                                                                                                                           |
| ------------------------------ | -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The grid's screen              | `projects` (21)            | **Exactly one request for a deep link that already carries a term**; the term written into the URL and nothing else driving the query; back restoring the unfiltered grid; both empty states with their own way out; the error panel replacing the grid; every card starred and kebabbed since US-909; **the star's own round trip** — the label naming the direction, nothing moving until the 204, and the request body a set rather than a toggle; and the whole `describe('paging')` block — 25 cards from one request, a page appended in server order, the control **removed not disabled** when everything is loaded, `aria-disabled` while busy and during a narrowing search, a keyboard press firing the same request, and focus landing on the last card, staying on the control while more remains, and staying where the reader put it mid-flight |
| The paged list                 | `project-list-store` (22)  | The first page at the screen's own take; `name=` when the URL carries one; **one request, not five**; a page appended and the offset moved by what came back; the term and the order riding it; a double press dropped; a press refused once everything is loaded; **a failed page keeping its cards and toasting**; a page **superseded rather than raced** by a newer query and by a delete moving the offset under it; created/renamed/deleted rows against the active search; and sign-out cancelling a page in flight |
| The dialog                     | `project-form-dialog` (9)  | Empty for a create; seeded from the project on an edit, **instructions included**; **re-seeded when the project arrives a round trip later**; `maxlength` derived from the schema; the **board's** duplicate copy rather than the server's; and the inline message cleared on the next keystroke with no imperative reset |
| The lifecycle store            | `project-actions-store` (10) | The whole body posted and nothing applied until the server confirms; a duplicate rendered inline and **never as a toast**, degrading to one only when the dialog was force-closed; the created project handed back to its invoker; **the fetch before opening on a listing row**; the full representation on a rename; instructions saved by echoing the rest; the deletion announced only on the 204; and a reopen refused while a row's action is in flight |
| The pure helpers               | `project-helpers` (8)      | `toProjectBody` echoing every field, and **"leave it" told apart from "clear it"**; the duplicate message swapped and every other one verbatim; the PascalCase key the server actually sends |
| The detail screen              | `project-detail` (7)       | The tab strip's file **and** conversation counts; **the conversation count still right while the reader is on Instructions**; the upload store resolved through the outlet, so the panel shares one; a finished upload becoming a row **while another tab is open**; leaving for the grid when the project is deleted under the reader; the guard **not trapping** them when it is; and the warning firing on a tab change |
| The composer host              | `project-composer-host` (7) | The conversation created inside the project and the prompt handed on; **`turnError` staying null**; a second press dropped rather than making two conversations; Stop cancelling and handing the prompt back; the 404 returning the reader to the grid; and a send refused before the route resolves |
| Standing instructions          | `instructions-panel` (7)   | Seeded from the only route that carries them; **settling only once the server confirms**; a clean panel left without a modal; a dirty one blocking until the reader decides; Escape read as keep-editing, **never as a silent discard**; and Cancel reverting in place |
| The files panel                | `project-files` (5)        | Rows with size and date; the drop zone, browse control and picker **with** the grant; **every add affordance withheld without it, files still downloadable**; the removal confirmed and the file named; and an attached file posted to the project's own upload route |
| The documents store            | `project-documents-store` (6) | **The bare array read with no envelope**; a row removed at once and **put back at its index** on failure; a second removal refused; refresh as the bridge from chip to row; and a previous project's response kept out of the new list |
| Relative time                  | `relative-time` (6)        | Minutes then hours inside the day; **"Yesterday" by the calendar, not by 24 hours**; the date fallback; a future timestamp read as "Just now"; and an unparseable value answering empty |
| The root lookup (US-307)       | `project-lookup-store` (13) | **Nothing requested until the bootstrap releases it**, and released only once however often `ensureLoaded` is called; every page of a multi-page set drained; the ceiling stopping it and saying so; an id resolved to its name and an unknown one to null; **the pages already drained kept when a later one fails**, which is where this store and the grid part company; a created project at the head where a refetch would put it; a rename adopted **without** evicting the row; a deleted project dropped so the picker cannot offer it; and sign-out emptying it and cancelling a drain in flight |
| The picker (US-307)            | `project-picker` (10)      | **A dialog holding a listbox, not a menu**; the assigned project marked selected and every other one not; the list filtered as the reader types **with no request**; the move issued for the project chosen; **Remove offered only for a conversation that is in a project**, and sending an explicit null when it is; a project created from the pinned action handed straight to the move; **the ceiling stated when the drain stopped short**; Escape closing and returning focus to the anchor; and a move refused while the row's own action is in flight |
| The conversations store (US-908) | `project-conversations-store` (14) | **One filtered request, with no drain and no ceiling**; nothing requested until the route resolves a project; the tab count withheld until the list has been read; a page appended rather than replacing the rows, and **a failed page keeping them**; the rows replaced when the reader moves to another project; **a row dropped when it is moved away — including from this panel's own kebab**; a row dropped when it is removed from every project; a rename or favourite adopted in place; a deletion shifting the counter; **a re-read when a conversation is moved _into_ this project from elsewhere**; a conversation moved between two other projects ignored; and sign-out cancelling the request in flight |
| The conversations panel (US-908) | `project-conversations` (11) | Frame `4g`'s four columns, **with no search field and no selection**; the five row actions with **Remove always present**; a removal going through `toUpdateBody` and dropping the row; the picker opened from the row menu; the empty state pointing at the composer above it; **no ceiling notice, because US-907 landed with it**; Load more shown only while there is more; the error panel replacing the list; a row left alone while its own action is in flight; a starred row kept, on the app's one synthesized payload; and **focus recovered when a row evicts itself** |

Plus twenty-one added to files this epic does not own: eight in `conversation-actions-store.spec.ts` (the move's three transitions, the rollback, the double-click refusal, the same-project no-op, a row the sidebar does not hold, and a sign-out mid-move), five in `composer.spec.ts` (the pill withheld until there is something to move, the project named, "No project", the second press closing the panel, and the pill disabled mid-turn), three in `conversations.spec.ts` (the chip named, **no chip past the lookup ceiling**, and the five row actions), two each in `chat.spec.ts` and `conversation-list-store.spec.ts`, and one in `conversation-row.spec.ts` (the picker anchored where the kebab was).

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 1385 specs
npm run lint    # ESLint (including the layering rule) + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs
npm run format  # Prettier
```

> **Verification status.** As with every epic before it, none of this has been exercised against a live API. The gates above are green and every behaviour here is asserted against `HttpTestingController` and `RouterTestingHarness`.

## 13. Deliberately not here

Both are recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table, so neither quietly becomes permanent.

| Missing                                                            | Owner   | Notes                                                                                                                                                    |
| ------------------------------------------------------------------ | ------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ~~**Any sort control** on the grid, and frame `4d`'s ceiling wording~~ | US-902  | Shipped, 2026-08-20 — four options over `GET api/projects`'s `sort=`/`dir=`, never disabled, with a notice naming the truncated order rather than frame `4d`'s original "unavailable" sentence. The notice itself went with the ceiling on 2026-08-29 (§4.1, §4.3) |
| ~~**The favourite star** on a card and in the detail header~~      | US-909  | Shipped, 2026-08-20 — confirm-then-render on both surfaces, through `ProjectActionsStore.toggleFavorite` (§5.7). The sidebar's own **Favorite projects** section is US-910's, documented in [Shell and Navigation §5.7](shell-and-navigation.md#57-favourite-projects-in-the-sidebar-us-910) rather than here, since it lives in `features/shell/` |

Four rows left this table with US-307 and US-908, and they are worth naming because each was a forecast that came true as written: the **Conversations tab** (§10), the **composer's project picker** (§9.4), the **dialogs living in `Shell`** (§5.6), and **`SessionBootstrap` requesting projects** — US-202's fourth criterion, which waited until a root project list had a consumer and got one in `ProjectLookupStore` (§9.1). US-908's own interim behaviour, the 500-item drain ceiling, never shipped at all: US-907 landed with the story (§10.1). The grid's own ceiling did ship, and was retired on 2026-08-29 (§4.1).

One thing the composer still does not offer, and it is a scope boundary rather than an omission: **the pill is absent on empty `/chat`**, because a conversation that does not exist yet has nothing to move (§9.4).

## 14. Troubleshooting

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
| The Load more control is missing while cards are clearly missing               | `hasMore()` is `skip < totalCount`; a card removed without `shiftCounters` moving **both** leaves the two agreeing when they should not (§4.1) |
| Cards from an old search term appear under a new one                           | `_querySuperseded$` was dropped, or `loadMore()` lost its `isPending()` guard — `switchMap` cancels the query, not a page already on the wire (§4.1)                           |
| A newly starred card does not jump to the top under Favourites first, or a new project does not sort into place under Alphabetical | Expected. Neither the grid nor `ProjectLookupStore` re-sorts in place on a write — the affected row stays where it was, and the true order settles on the next query (§4.1, §5.7) |
| Every card re-renders on each change-detection pass                            | `[link]` was bound to a fresh `[route, id]` array, or an "Updated …" label reads `Date.now()` from the template (§4.3)                                                        |
| A card's kebab item blurs to `<body>` when pressed                             | `aria-disabled` was replaced with the native `disabled` attribute (§4.3)                                                                                                      |
| The Files tab count reads 0 on a project that has files                        | The count was rendered before `isFulfilled()`. It is withheld until the list has actually been read (§6.1)                                                                    |
| Files uploaded from the composer never appear in the Files tab                 | The chip-to-row bridge was moved onto the files panel, where the effect only runs while that panel is mounted (§7.3)                                                          |
| A tab switch cancels an upload in progress                                     | `UploadStore` was provided on the files panel instead of on `ProjectDetail` (§7.1)                                                                                            |
| A removed file reappears at the top or bottom of the list                      | The optimistic rollback lost its captured index, or restored into a list a refetch had already replaced (§7.2)                                                                |
| The unsaved-instructions modal traps a reader on a deleted project             | The `deleted` short-circuit came off `confirmDiscard`. `deleted` is a latch and the navigating effect will not re-run (§6.3)                                                  |
| The discard modal asks twice, or discards after "Keep editing"                 | `settleDiscard` lost its idempotence — `(closed)` fires after `discard` has already answered (§6.3)                                                                            |
| A project dialog is still open after the browser's Back button                 | Known and accepted since US-307 moved the dialogs into `Shell`, which is not unmounted while signed in. The conversation dialogs have always behaved this way; fixing one means fixing both (§5.6)  |
| Two projects are created from one double-click                                 | `_create` was moved off `exhaustMap`. Their names then collide with each other (§5.1)                                                                                          |
| A second project delete is silently dropped                                    | `_delete` was moved onto `exhaustMap`. Deletes of different projects are independent (§5.1)                                                                                   |
| A project chip or the composer pill shows nothing for a conversation that is in a project | `nameOf` answered null: the project is past the 500-item drain ceiling, or the lookup drain failed before reaching it. Rendering nothing is correct — a guid is not a name (§9.1) |
| The picker's list is empty, or shows an error, on every surface at once        | One `ProjectLookupStore` serves them all. Check the drain, not the panel — and note that a mid-drain failure deliberately **keeps** the pages already loaded (§9.1)          |
| Typing in one picker narrows another one on screen                             | The term was moved onto the lookup store. It is per-component state for exactly this reason (§9.2)                                                                            |
| Arrow keys move the caret instead of the list, or Tab cycles inside the picker | The panel was made a `Menu`, or the roving-focus handler was dropped. The field is the panel's only tab stop and the rows are `tabindex="-1"` (§9.2)                          |
| A conversation moved into the open project does not appear on its panel        | The `updated` handler's "not held, but now matches" branch was removed. It is asymmetric on purpose (§10.2)                                                                    |
| A row stays on the panel after being moved out of the project                  | The other half of the same handler. A move from this panel's own kebab is the common case (§10.2)                                                                             |
| The Conversations tab count reads 0 from the Instructions tab                  | `ProjectConversationsStore` was provided on the panel instead of on the detail route (§10.1)                                                                                   |
| The card grid is one column on a wide screen                                   | A Bootstrap `row`/`col-*` class came back. That grid is not imported; the layout is the intrinsic `auto-fill` track (§4.3)                                                     |
| A screen's content is cut off below the fold and will not scroll               | The routed host lost `overflow-y: auto`, or its `min-height: 0` (§4.3)                                                                                                        |
| The project-detail tabs render as pills on a phone rather than accordions      | That is the intended behaviour and a recorded departure from frame `4e` (§6.1)                                                                                                |

## 15. Key files

| Concern                     | File                                                                                                                                                                                                                                                    |
| --------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Wire shapes and field caps  | [`domain/api/project.ts`](../../enterprise-gpt-ui/src/app/domain/api/project.ts)                                                                                                                                                                        |
| Date labels                 | [`domain/format/relative-time.ts`](../../enterprise-gpt-ui/src/app/domain/format/relative-time.ts)                                                                                                                                                       |
| Update body, duplicate name | [`core/projects/project-update.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-update.ts), [`duplicate-name.ts`](../../enterprise-gpt-ui/src/app/core/projects/duplicate-name.ts)                                                            |
| Page size, ceiling, term match | [`core/projects/project-load.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-load.ts)                                                                                                                                                      |
| Lifecycle store             | [`core/projects/project-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-actions-store.ts)                                                                                                                                       |
| The root lookup             | [`core/projects/project-lookup-store.ts`](../../enterprise-gpt-ui/src/app/core/projects/project-lookup-store.ts)                                                                                                                                        |
| Events                      | [`core/events/project-events.ts`](../../enterprise-gpt-ui/src/app/core/events/project-events.ts)                                                                                                                                                         |
| Composer contract, pending prompt | [`core/chat/composer-host.ts`](../../enterprise-gpt-ui/src/app/core/chat/composer-host.ts), [`pending-prompt-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/pending-prompt-store.ts)                                                        |
| Shared history rule         | [`core/navigation/search-history.ts`](../../enterprise-gpt-ui/src/app/core/navigation/search-history.ts)                                                                                                                                                 |
| The composer, and its project pill | [`shared/composer/composer.ts`](../../enterprise-gpt-ui/src/app/shared/composer/composer.ts)                                                                                                                                                      |
| The picker, and shared panel placement | [`shared/projects/project-picker/project-picker.ts`](../../enterprise-gpt-ui/src/app/shared/projects/project-picker/project-picker.ts), [`shared/overlay/anchored-panel.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/anchored-panel.ts)  |
| Routes and layout           | [`features/projects/projects.routes.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.routes.ts), [`projects-layout.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects-layout.ts), [`detail/project-detail.routes.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-detail.routes.ts) |
| Grid                        | [`features/projects/projects.ts`](../../enterprise-gpt-ui/src/app/features/projects/projects.ts), [`project-list-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/project-list-store.ts)                                                    |
| Dialogs                     | [`features/shell/project-actions/project-form-dialog.ts`](../../enterprise-gpt-ui/src/app/features/shell/project-actions/project-form-dialog.ts), [`delete-project-dialog.ts`](../../enterprise-gpt-ui/src/app/features/shell/project-actions/delete-project-dialog.ts) |
| Detail screen               | [`features/projects/detail/project-detail.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-detail.ts), [`project-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-store.ts)                             |
| Instructions                | [`detail/instructions/instructions-panel.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/instructions/instructions-panel.ts), [`unsaved-instructions.guard.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/instructions/unsaved-instructions.guard.ts) |
| Files                       | [`detail/files/project-files.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/files/project-files.ts), [`project-documents-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/files/project-documents-store.ts)           |
| Compose in a project        | [`detail/project-composer-host.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/project-composer-host.ts)                                                                                                                                   |
| A project's conversations   | [`detail/conversations/project-conversations.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/conversations/project-conversations.ts), [`project-conversations-store.ts`](../../enterprise-gpt-ui/src/app/features/projects/detail/conversations/project-conversations-store.ts) |
| The layering rule           | [`eslint.config.mjs`](../../enterprise-gpt-ui/eslint.config.mjs)                                                                                                                                                                                         |
| The API side                | [`Enterprise.Gpt.Api/Endpoints/ProjectEndpoints.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ProjectEndpoints.cs), [`ProjectService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ProjectService.cs)                               |
| Related reference           | [Projects (server)](../projects/project-management.md), [Conversation Library](conversation-library.md), [File Attachments](file-attachments.md), [Conversation Actions](conversation-actions.md), [Frontend Foundation](frontend-foundation.md), [Shell and Navigation](shell-and-navigation.md), [Design System](design-system.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
