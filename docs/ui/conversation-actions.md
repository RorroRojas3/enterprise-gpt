# Conversation Actions

How the rebuilt Angular client at `enterprise-gpt-ui/` renames and deletes conversations: one root store owning both flows, two dialogs mounted once in the shell, a second event group carrying server-confirmed outcomes to the chat route, and the application's first Signal Form.

Audience: a developer adding the next per-conversation action (US-305 favourite and US-307 project moves extend exactly these seams), building any of the list stores that will copy the optimistic/rollback semantics, or writing the application's next form. Read [Shell and Navigation](shell-and-navigation.md) first for `ConversationListStore`, the reference list store these flows mutate, and [Frontend Foundation](frontend-foundation.md) for the store features and the `core/errors` conventions everything here composes.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Three stories landed together and close phase P2 of the rebuild:

| Story | What it delivers |
| --- | --- |
| **US-304** | Rename, from the sidebar row kebab: frame `3c`'s 420 px modal with a live 256-character counter, optimistic rename with rollback, server validation inline — the first Signal Form |
| **US-306** | Delete, from the same kebab (the only red item): frame `3d`'s confirmation naming the conversation, optimistic removal with position-faithful restore, navigation off a deleted open conversation |
| **US-308** | The 52 px conversation header gains a kebab routing Rename and Delete through the same flows — nothing reimplemented |

Six decisions shape everything here, and each looks removable until you know what it prevents:

1. **One root store owns both flows.** `ConversationActionsStore` is `providedIn: 'root'` because its invokers live in two features that must not import each other — the sidebar row (`features/shell`) and the chat header (`features/chat`). Everyone talks to the store; nobody talks to a dialog (§3).
2. **The dialogs are mounted once, in the shell.** The shell wraps both invokers, so one `<app-rename-conversation-dialog>` and one `<app-delete-conversation-dialog>` serve every surface (§3.1).
3. **Entity writes go through `ConversationListStore` methods.** Its state is protected, so no other store can `patchState` it — and should not, because *how* a row is patched is the list's decision. The actions store decides *when* (§3.2).
4. **Server-confirmed outcomes travel as events.** `conversationEvents.updated` and `.deleted` are the second `@ngrx/signals/events` group, and they exist for a reachability reason, not a stylistic one: a root store cannot inject the route-scoped `ConversationStore` (§6).
5. **Every conversation update goes through `toUpdateBody`.** `PUT api/conversations` is a full-representation PUT — a body that omits `projectId` unlinks the conversation from its project — so one builder echoes it always, and no caller can forget (§4.1).
6. **Per-row in-flight state comes from `withPendingIds`.** A rename on one conversation disables that conversation's menu items — via `aria-disabled`, never the native attribute — and no one else's (§7).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| Both flows: state, guards, optimistic patches, rollbacks | [`core/conversations/conversation-actions-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-actions-store.ts) |
| The `PUT api/conversations` body builder | [`core/conversations/conversation-update.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-update.ts) |
| The entity mutation surface the flows call | [`core/conversations/conversation-list-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-list-store.ts) — `renameRow`, `refreshRow`, `setRowPending`, `removeRow`, `restoreRow` |
| Server-confirmed conversation events | [`core/events/conversation-events.ts`](../../enterprise-gpt-ui/src/app/core/events/conversation-events.ts) |
| Server validation → Signal Forms | [`core/errors/server-messages.ts`](../../enterprise-gpt-ui/src/app/core/errors/server-messages.ts) — `serverMessagesFor`, `unmatchedServerMessages` |
| Per-row in-flight tracking | [`core/state/with-pending-ids.ts`](../../enterprise-gpt-ui/src/app/core/state/with-pending-ids.ts) |
| The rename dialog (the first Signal Form) | [`features/shell/conversation-actions/rename-conversation-dialog.ts`](../../enterprise-gpt-ui/src/app/features/shell/conversation-actions/rename-conversation-dialog.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/shell/conversation-actions/rename-conversation-dialog.html) |
| The delete confirmation | [`features/shell/conversation-actions/delete-conversation-dialog.ts`](../../enterprise-gpt-ui/src/app/features/shell/conversation-actions/delete-conversation-dialog.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/shell/conversation-actions/delete-conversation-dialog.html) |
| The sidebar row kebab | [`features/shell/sidebar/conversation-row.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/conversation-row.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/conversation-row.html) |
| The header kebab, and navigating off a deleted conversation | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts), [`chat.html`](../../enterprise-gpt-ui/src/app/features/chat/chat.html) |
| The header's `current` DTO and `updated` handling | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts) |
| The dialogs' single mount point | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts) |

## 2. Quick start

### 2.1 Adding a conversation action to a menu

Inject `ConversationActionsStore` and call a `begin*` method with the row's DTO. That is the entire integration — the store owns the dialog, the request, the optimistic patch and the failure path:

```html
<app-menu [label]="'Actions for ' + conversation().name">
  <button
    appMenuItem
    type="button"
    [attr.aria-disabled]="pending() ? 'true' : null"
    (click)="actions.beginRename(conversation())"
  >
    Rename
  </button>
</app-menu>
```

`aria-disabled` rather than the native attribute, because a natively disabled item cannot take focus and strands the menu's roving focus. The `begin*` methods refuse a pending row themselves, so the click is a guarded no-op, not a hole (§7).

### 2.2 Adding the next action (US-305, US-307)

The seams the next stories extend, in order:

1. A method trio on `ConversationActionsStore` (`begin*` guard → optimistic patch through a `ConversationListStore` method → request → `conversationEvents.updated` on success, rollback + `ToastStore.fromError` on failure).
2. Any update that touches `PUT api/conversations` builds its body with `toUpdateBody(current, changes)` — never by hand (§4.1).
3. Menu items in *both* kebabs — `conversation-row.html` and `chat.html` — carrying the same `aria-disabled` binding.

## 3. One store, two invokers, dialogs mounted once

`ConversationActionsStore` holds five pieces of state: `renameTarget`, `renameBusy`, `renameError`, `renameRejectedName`, and `deleteTarget`. A non-null target *is* the open dialog — `RenameConversationDialog` and `DeleteConversationDialog` are mounted once in `Shell` and each computes `open` from its target. Opening, cancelling and submitting are store methods, so the sidebar row and the chat header share the dialogs without either feature importing the other, and without the dialogs knowing who invoked them.

Concurrency differs between the flows, deliberately:

- **Rename runs under `exhaustMap`.** A second submit while one is in flight is a double-click, not a second rename; `renameBusy` disables the buttons and `exhaustMap` closes the race under them. The optimistic patch is made *inside* the projection, so an emission `exhaustMap` drops leaves no patch behind with no request to resolve it.
- **Delete runs under `mergeMap`.** Deletes of *different* conversations are independent and may overlap — `exhaustMap` would silently drop the second. Same-id overlap cannot happen: the row and its dialog are gone, and `beginDelete` guards on the pending id.

**`renameBusy` tracks the request, not the dialog.** A dialog force-closed mid-flight (a second Escape is uncancelable in Chromium) leaves it `true` until the request settles, so a reopened dialog honestly reports that `exhaustMap` is still occupied instead of swallowing the next submit silently. It is cleared in exactly one place — `tapResponse`'s `finalize`, which runs on success, failure and sign-out cancellation alike — and `cancelRename` deliberately does not touch it.

### 3.1 The mount point

`Shell` renders both dialogs beside the routed screen. Do not mount a second copy in a feature: the store's `renameTarget` would open both.

### 3.2 The mutation surface on `ConversationListStore`

`protectedState` means the list's entities can only be written by the list's own methods, so US-304/306 added a small, deliberate surface: `renameRow` (optimistic; rolled back by calling it again with the previous name), `refreshRow` (adopt the server's DTO — its `dateModified`, not a local guess), `setRowPending`, `removeRow` and `restoreRow` (§5.1). Rows the list does not hold are ignored by all of them except `removeRow`, which returns `null` — the deep-link case, where the DELETE still goes out and there is nothing to restore.

## 4. Rename (US-304)

The flow, end to end: `beginRename` (refused while the row is pending) → the frame `3c` modal, seeded with the current name → `submitRename` trims, treats an unchanged name as a close and an empty one as a no-op (the dialog's disabled submit is UI; these guards are what make it unbypassable) → optimistic `renameRow` → `PUT api/conversations` → on success `refreshRow(updated)`, dispatch `conversationEvents.updated(updated)`, close; on failure roll the name back and either render the validation problem inline or toast.

A validation problem renders inline **only while the dialog is still up**. If the user cancelled before the 400 landed there is no field to render under, so it degrades to a toast rather than being dropped — the single deliberate exception to "`ToastStore.fromError` is the only place an `AppError` becomes a toast", because `fromError` silences validation problems on the assumption they render inline.

### 4.1 `toUpdateBody` and the full-representation PUT

`PUT api/conversations` replaces the whole conversation (the id travels in the body, not the route), so a body that omits `projectId` — or sends `null` — removes the conversation from its project. Every update path goes through one builder so no caller can unlink a project by forgetting to echo the field. This is US-307's stated invariant, in force early because US-304 is the first update path:

```ts
toUpdateBody(current, { name });                    // echoes current.projectId
toUpdateBody(current, { projectId: null });         // explicitly unlinks
toUpdateBody(current, { projectId: undefined });    // echoes — "looks omitted" never means "remove"
```

Only an explicit `null` unlinks. An `undefined` — including one passed explicitly, as `{ projectId: selectedProject()?.id }` naturally produces — echoes the current value, because "looks omitted" behaving as "remove" is exactly the accident the builder exists to prevent.

The header kebab feeds the builder `ConversationStore.current`, which prefers the detail DTO over the sidebar's copy precisely because its `projectId` is the fresh one a rename must echo — and stays `null` for a deep link the list never held, so the kebab waits one round trip rather than acting on a guess.

### 4.2 The first Signal Form

The rename dialog is the application's first form built on `@angular/forms/signals`, and a recorded product-owner decision (2026-08-11) makes Signal Forms the standard for every form that follows — never `ReactiveFormsModule` or `FormsModule` in new code. The API is `@experimental` in Angular 21.2, so it is re-checked against the installed types on every Angular upgrade; `@angular/forms/signals/compat` is the bridge if an integration ever demands an `AbstractControl`.

The shape later forms copy:

- **A `signal` model, reseeded on open.** The component is mounted once and lives as long as the shell, so an `effect` re-seeds the model from the target each time the dialog opens and calls `f().reset()` to clear `touched`/`dirty` left over from a previous open.
- **Schema rules, not imperative validators**: `required`, `maxLength(256)`, and a `validate` rule for the server's verdict (§4.3).
- **`[formField]` bindings**, and *no* literal `maxlength` attribute on the input — the directive owns the constraint attributes and derives that one from the schema rule (a literal duplicate is template diagnostic NG8022).
- **Validity is consulted on submit** even where today's rules are all backstopped elsewhere, because this is the form later stories copy and the first rule without a native backstop would otherwise submit invalid values straight to the server.
- **Errors render after interaction — or immediately when they came from the server**, which no amount of prior interaction predicts. Submit calls `markAsTouched()` first, because Enter submits without a blur and a client-rule message would otherwise stay hidden exactly when it matters.

### 4.3 `serverMessagesFor`: server validation without `setErrors`

Signal Forms has no `setErrors`, so the server's verdict cannot be written onto a control the way `applyServerErrors` does for `AbstractControl`. Instead it surfaces declaratively:

1. On a 400, the store records the `ValidationAppError` as `renameError` and the exact submitted name as `renameRejectedName`.
2. The form's `validate` rule compares the field's trimmed value against `renameRejectedName` — trimmed, because the store submits the trimmed value and the field may hold surrounding whitespace the server never saw — and, on a match, maps `serverMessagesFor(renameError, 'name')` into form errors.
3. The moment the user edits the value, the comparison fails and the message clears itself — no imperative reset anywhere. A side effect worth keeping: while the field still holds the rejected name the field is *invalid*, so resubmitting the identical doomed value is blocked too.

`serverMessagesFor` (in [`core/errors/server-messages.ts`](../../enterprise-gpt-ui/src/app/core/errors/server-messages.ts)) is the Signal Forms counterpart to `applyServerErrors`: it normalizes the server's PascalCase `errors` keys with .NET's own camel-casing algorithm before comparing them to camelCase field names, with a case-insensitive match as the fallback. It handles flat keys only, deliberately — the first form that binds a nested or indexed path (`Chunking.MaxTokens`, `Files[0].FileName`) extends it with `applyServerErrors`' path parsing rather than reimplementing it. `applyServerErrors` itself gains no new call sites.

Its companion `unmatchedServerMessages` returns messages under keys the form does not model — object-level rules, which FluentValidation keys with the empty string, and properties the dialog never sends wrong. The dialog renders them in the same error region rather than dropping them, because a server message the user cannot see is a rename that silently fails.

## 5. Delete (US-306)

The flow: `beginDelete` (refused while the row is pending) → frame `3d`'s confirmation, which names the conversation and states what goes (messages) and what stays (uploaded files); initial focus lands on **Cancel**, because Enter on a freshly opened destructive confirm must not destroy anything, and Delete is the only red item → `confirmDelete` closes the modal and removes the row **at once**, while the request settles in the background → a 204 raises the success toast and dispatches `conversationEvents.deleted(id)`; a failure restores the row and toasts.

The API always answers 204 — the DELETE is idempotent, never a 404 — so the only failures on this path are transport and 5xx.

### 5.1 `removeRow` / `restoreRow`: a position-faithful, guarded rollback

`removeRow` captures everything `restoreRow` needs to undo it — the DTO, its index, and the **query generation** it was removed from — and does three more things than deleting an entity:

- **Shifts the pagination window back.** The server no longer counts the row, so `skip` and `totalCount` both decrement (clamped at zero, because a page response landing between remove and restore can already have moved them). Without this, the next "Load more" would ask for a window one row past where the server now is.
- **Supersedes any "Load more" already on the wire.** That page's URL was built against the pre-removal offset; if the server deletes first, the row that slid into the last fetched position would fall between the windows — a permanent one-row hole. Cancelling lets the next press use the corrected offset.
- **Returns `null` for a row the list does not hold** (a deep link beyond the first page). The DELETE still goes out; there is simply nothing to restore.

`restoreRow` reinserts at the captured index (clamped, because pages may have appended meanwhile) — but **refuses outright** when the removal's generation no longer matches the list's, i.e. the user searched while the delete was in flight. The row may not match the query on screen, and the counter bump would starve a genuine row out of the next "Load more" window. The error toast has already reported the failed delete; the row reappears with the next refetch of a result set it belongs to. It is also a no-op when the id is somehow already back.

### 5.2 Navigating off a deleted open conversation

Deleting the conversation that is open must land the user on the empty chat screen — but only when the delete **completes**. The `deleted` event fires only on the 204, so the navigation is never optimistic, and a failed delete never bounces the user off a conversation that still exists. `Chat` subscribes and compares the payload against its `conversationId` route input — the authoritative "is this the one open" fact — so a user who moved to another conversation mid-flight is left alone. `chatMatcher` serves `/chat` and `/chat/{id}` from one route configuration, so the navigation is an input change, not a remount.

Both close paths repair focus, because confirming a delete destroys the element focus would return to. The dialog falls back to `#main-content` (the always-present programmatic focus target that already serves the skip link) when focus fell through to `<body>` or was stranded in the closed dialog; `Chat` does the same after the navigation, inside `afterNextRender`, because the router's promise resolves before zoneless change detection has removed the header — an immediate check would still find the kebab connected and skip the fixup. In both places only a fall-through is corrected; a user who clicked elsewhere mid-flight is not yanked back.

## 6. `conversationEvents`: the second event group

The Events plugin is a deliberate opt-in in this codebase, and `conversationEvents` qualifies for the same reachability reason `sessionEvents` did: `ConversationActionsStore` is root-scoped, while `ConversationStore` is scoped to the chat route — a root store cannot reach a route-scoped one by method, and registering route stores with a root service would invert the dependency. Anything with a single observer stays on plain store methods; these events exist because the sidebar list and the open conversation must both react without knowing each other.

Two events, both carrying **server-confirmed facts only**:

| Event | Payload | Dispatched | Observers |
| --- | --- | --- | --- |
| `updated` | The `ConversationDto` the server returned | On a rename's 200 (US-304); favourite (US-305) and project moves (US-307) will follow | `ConversationStore` patches its detail copy so the header name stays fresh — this covers the deep-link case `refreshRow` ignores, and the spread keeps `mcpServerIds`, which `ConversationDto` does not carry |
| `deleted` | The conversation id | Only on the DELETE's 204 (US-306) | `Chat` navigates off the deleted open conversation (§5.2) |

Optimistic patches and their rollbacks stay inside `ConversationListStore` — an event observer never has to undo one. Keep it that way: the moment an event can describe something the server has not confirmed, every observer inherits the rollback problem.

## 7. Per-row pending: `withPendingIds` in practice

US-304 is the first per-row action, and with it `withPendingIds` — built in US-104 — is composed into `ConversationListStore` for the first time. The contract:

- `ConversationActionsStore` sets the flag around each request via `setRowPending`, **always through `tapResponse`'s `finalize`** to clear it, so it releases on success, failure and sign-out cancellation alike. For a delete, pending is set *before* `removeRow`, so the header kebab stays disabled for the whole flight even though the sidebar row disappears immediately.
- The `begin*` guards are the real gate: an action on a pending row is refused in the store, not merely discouraged in the template.
- The templates render the flag as `aria-disabled` on the menu items — the sidebar row via its `pending` computed, the header via `ConversationStore.actionPending` — never the native `disabled` attribute, which would make the item unfocusable and break the menu's roving focus and Escape handling.

One trap, inherited from the feature itself: the pending-id updaters replace the `Set` rather than mutating it, and that is load-bearing — `patchState` compares by reference, so a mutated set produces no signal write at all. Nothing re-renders, nothing fails; the list just stops updating.

## 8. Deliberately not here

Recorded in the PRD and the build order, not omissions:

- **The favourite star** on the header and row — US-305, which will dispatch `conversationEvents.updated` through its own dedicated `PUT api/conversations/{id}/favorite` route.
- **The project move/remove items** in both kebabs — US-307, whose `projectId` invariant `toUpdateBody` already enforces (§4.1).
- **The sub-768 px collapse** of the header into frame `1d`'s mobile navbar — US-1403, which builds the responsive shell it collapses into.

## 9. Troubleshooting

| Symptom | Cause |
| --- | --- |
| A menu item "does nothing" | That conversation's own action is in flight: the `begin*` guards refuse a pending row. The item is `aria-disabled`, not natively disabled, so it still takes focus and clicks — by design (§7). |
| The inline server message never clears | The field still holds the exact rejected value — the `validate` rule compares the *trimmed* field value against `renameRejectedName`. If a new form copies the pattern, make sure it compares what the store actually submitted (§4.3). |
| The server message never *appears* | The key did not match the field. `serverMessagesFor` handles flat keys only; a nested or indexed key falls through to `unmatchedServerMessages`, so bind an error-summary region or the message is silently lost (§4.3). |
| A failed delete did not restore the row | The user searched while the delete was in flight: `restoreRow` refuses a restore into a different query generation, on purpose. The error toast still reported the failure; the row returns with the next refetch (§5.1). |
| A reopened rename dialog will not submit | `renameBusy` is still true: the previous request is still on the wire (a force-closed dialog does not cancel it) and `exhaustMap` is still occupied. It clears when the request settles (§3). |
| The row's pending flag sticks forever | A request path bypassed `tapResponse`'s `finalize`. Both flags — `setRowPending` and `renameBusy` — must clear there and only there (§7). |
| The header shows the old name after a rename via deep link | The `updated` event handler in `ConversationStore` is what covers that case — `refreshRow` ignores rows the list does not hold. Check the event is dispatched on the server's response, not optimistically (§6). |
