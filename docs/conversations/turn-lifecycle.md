# Conversation Turn Lifecycle

The layer above the [Conversation Streaming Client](streaming-client.md): what happens between pressing Send and a settled transcript entry, and what happens when a conversation is reopened. Shipped by nine stories: US-401 (send a first prompt; the conversation is created *around* it), US-406 (streamed answer rendering), US-407 (Stop, keeping the partial output) and US-501 (the chronological activity timeline) built the live turn; EP-4's closing five then finished it — **US-408** (the busy warning, §8.1), **US-412** (the MCP consent card, §8.2), **US-410** (reopening a conversation: its stored messages *and* the settings it last used, §7), **US-409** (the name the server generates after a first turn, §7.8) and **US-413** (dictation, §9.2). Four recorded deferrals closed along the way: US-303's suggested prompt chips, US-403's tool-server-unavailable card, the generic notice standing in for the busy and consent frames, and US-407's `composer__aux` dimming rule, which had matched no control until the microphone arrived.

Audience: whoever builds the stories on top — nested activities (US-502), reasoning (US-503), the consent action (US-411) — or debugs a turn that ended strangely. The wire format lives in the [contract](streaming-contract.md); the codec, transport, and turn settings live in the [streaming client](streaming-client.md); how the answer text becomes HTML, and how the page follows it, lives in [Answer Rendering](../ui/answer-rendering.md) — none of the three is restated here.

## 1. Overview

One route-scoped store owns everything: [`TurnStore`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) holds the session transcript, the live turn, and the send flow, provided by `Chat` beside `ConversationStore` — so its lifetime is the chat screen's, and leaving `/chat` unsubscribes the stream, which aborts the fetch (there is no stop endpoint; [streaming client §4.2](streaming-client.md#42-aborting--stop-sign-out-unsubscribe)).

| Concern | What it does | Where |
|---|---|---|
| Turn state | Phases, transcript entries, live snapshot + timeline, settle logic, history replay | `features/chat/turn-store.ts` |
| Arrival-order index | Which text blocks and activity cards arrived in what order | `domain/stream/turn-timeline.ts` — framework-free, beside the vendored fold |
| Replaying a stored answer | One persisted message → the same folded shape a live turn produces | `domain/stream/replayed-turn.ts` |
| Transcript rendering | Settled entries, the live turn, the notices, the stopped card, the history states | `features/chat/transcript/` |
| Send / Stop control | The morphing button, composer seeding, focus recovery | `features/chat/composer/` |
| Dictation | The Web Speech session, the elapsed clock, the phrase handoff | `core/speech/speech-recognition.ts`, `features/chat/composer/dictation-store.ts` |
| "A turn completed" | The one fact this screen publishes to the rest of the app | `core/events/turn-events.ts` |
| Landing screen | The wordmark, prompt heading, and four suggested prompt chips | `features/chat/chat-empty-state.ts` |

Two structures describe one live turn, and neither duplicates the other (§4). Reopening a conversation replays its stored messages (§7) — but only the messages: the **activity timeline is session state**, because the stream that produces it is never persisted, and that is permanent rather than pending (§7.5).

## 2. The turn state machine

`TurnPhase` is one value, and everything the screen shows during a turn derives from it:

| Phase | Meaning | What shows |
|---|---|---|
| `idle` | No turn in flight | The Send control; transcript or empty state |
| `creating` | `POST api/conversations` is out for a first prompt | The thinking ridgeline (frame `1e`) |
| `awaitingFirst` | The stream request is out; nothing renderable has arrived | The thinking ridgeline |
| `streaming` | Content is arriving | The live turn, with the blinking caret |
| `stopping` | The user pressed Stop; the abort's error is on its way | The live turn, until the settle lands |

Three computed signals read it: `showThinking` (`creating` or `awaitingFirst`), `showLiveTurn` (`streaming` or `stopping`), and `inFlight` (anything but `idle`) — which is also what morphs the composer's button into Stop (§9).

Reading a conversation's stored messages is deliberately **not** a phase (`historyPending` / `historyError` are their own state, §7.1): no turn is in flight while history loads, the composer stays usable throughout, and folding it into `TurnPhase` would have put both facts at risk.

`awaitingFirst` advances to `streaming` only when a batch carries **renderable** content — an `ActivityStarted` or a `TextDelta`. A batch of `Status` or `ReasoningDelta` events deliberately does not clear the thinking indicator. US-406 wrote that rule when this app emitted no request-level statuses at all; that rationale is now historical — the server opens every turn with a synthetic `Status` (`"Starting"`) and a server-authored `ReasoningDelta` ([contract §4.3](streaming-contract.md#43-a-turn-frame-by-frame)) — but the rule now does real work: without it, those two frames would dismiss the ridgeline with nothing renderable to replace it. The client does not yet render `assistantStatus` or reasoning text (reasoning rendering is US-503, P4), so the opening pair is on the wire but invisible here today — by design, not omission.

## 3. Sending, and creating the conversation around the first prompt

The send pipeline is one `rxMethod` on `exhaustMap` — the double-send guard. A send while a turn is in flight is *ignored*, not queued: a queued one would only meet the server's 409, because a conversation runs one turn at a time ([contract §6.3](streaming-contract.md#63-it-is-not-resumable-and-not-multiplexed)). `send(prompt)` also re-checks what the composer's disabled button already enforces — no selection resolved or empty text is a no-op, so no caller can issue a turn without a `modelId`.

### 3.1 The unbound send

"New conversation" creates nothing — the empty screen is just a screen, and no conversation exists until the first prompt is sent (US-401, US-302). When a send finds no `boundConversationId`, the store:

1. Enters `creating` and POSTs `api/conversations` with `{ "projectId": null }`.
2. On the 201: prepends the new conversation into `ConversationListStore` (so the sidebar row and the header's list-fallback name exist the moment the URL updates), patches `boundConversationId`, **then** navigates.
3. Navigates with `router.navigateByUrl('/chat/{id}', { replaceUrl: true })` and streams the turn against the new id.

The navigation is a **recorded deviation from US-401's letter** (product-owner approved, 2026-08-12). The story says `Location.replaceState`, but that moves the URL without moving the Router: the sidebar's `routerLinkActive` selection breaks, "New conversation" becomes a same-URL no-op, and the route's `conversationId` input never binds. `replaceUrl: true` keeps the story's *intent* — history replaced, no remount, because one route configuration serves both `/chat` and `/chat/{id}`.

The ordering in step 2 is load-bearing. `bindRoute` follows the route's `conversationId` input and resets everything on a change of conversation — but it treats an incoming id equal to `boundConversationId` as "the URL caught up with our own create" and passes it through as a no-op. Setting the id *before* navigating is what makes the store's own navigation indistinguishable from no change at all.

### 3.2 When the create goes wrong

- **The POST fails** — the prompt goes straight back into the composer (US-401's "not lost") and a failure notice renders *without* a Retry button, because the reseeded composer is the retry surface. The pipeline returns `EMPTY` so the `rxMethod` stays alive for the next send.
- **The user presses Stop, or navigates away, mid-create** — the POST is cancelled (it accepts no `AbortSignal`, so a `takeUntil` subject cancels it) and the prompt is reseeded. A route event during `creating` is always foreign — the store's own navigation cannot fire before the 201 has moved the phase on — so `bindRoute` cancels the create before resetting; without that, the 201 would land after the reset, clobber the new binding, and navigate the user off the conversation they just opened.
- **Sign-out mid-create** — the same `takeUntil` races `injectSignedOut()`, and `withResetOnSignOut` (composed last, per the repo rule) clears the state.

## 4. Folding a live turn: the snapshot and the timeline index

Each delivered batch — at most one per ~16 ms flush, at any token rate ([streaming client §4.4](streaming-client.md#44-batching)) — folds through **two** reducers in **one** `patchState`: the vendored `foldAssistantEvents` into the snapshot, and [`foldTimeline`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.ts) into the arrival-order index. The apply is guarded to in-flight phases, so a batch racing a reset cannot resurrect a cleared turn; batches landing while `stopping` still fold, because they are the partial output the stopped card keeps (§6).

### 4.1 Why an index beside the fold

`foldAssistantEvents` owns every piece of activity *state* — labels, kinds, progress, completion — but its snapshot is hierarchical and unordered across text: it can say a tool ran and finished, but not whether its card arrived before or after a paragraph. US-501 needs exactly that interleaving, so `TurnTimeline` records **only** the ordering:

- A `text` node is a half-open `[start, end)` slice into the folded snapshot's single `text` string. The timeline never copies answer text, so a delta costs O(1) here regardless of transcript length; consecutive deltas extend the last text node rather than opening a new one.
- An `activity` node is a `scopeId` reference into the snapshot's activity tree, resolved at render time with `findActivity` (recursive, any depth). `parentScopeId` is recorded now so US-502 can drop child nodes and render them inside their parent's card without touching the reducer.

Neither structure duplicates the other — the timeline holds no text and no activity state, the snapshot holds no ordering. Both reducers apply the same `?? ''` fallbacks as the vendored fold, so offsets always index `snapshot.text` and the render-time join cannot miss on an event the fold accepted. Every `ActivityStarted` becomes its own node regardless of depth, because US-501 renders nested work as separate chronological cards; nesting is US-502's.

The join happens once per flush in `AssistantTurn`'s `renderedNodes` computed — slices of `snapshot.text` for text nodes, `findActivity` lookups for activity nodes — not per node in the template. An activity node whose scope the snapshot cannot resolve is dropped, defensively only, since both structures are fed the same events.

## 5. Settling: how a turn ends

The transport ends a turn in exactly two ways — `complete`, or `error(AppError)` ([streaming client §4](streaming-client.md#4-the-transport)) — and the store discriminates each against the phase and the folded snapshot. This table is the heart of US-406 and US-407:

| Stream ending | Phase at settle | Outcome |
|---|---|---|
| `complete`, `Finished` was folded (`snapshot.phase === 'Completed'`) | any in-flight phase — **including `stopping`** | **One atomic append** of the user and assistant entries; live turn cleared |
| `complete`, no `Finished` | `awaitingFirst` / `streaming` | Cut-off entry (user + assistant-with-warning, keeping snapshot, timeline, and a Retry) |
| `complete`, no `Finished` | `stopping` | The stopped card — the server closed the body just as Stop was pressed; visually this is the stop the user asked for, not a cut-off warning |
| `error` `aborted` | `stopping` | The stopped card: detached, "Stopped — not saved to this conversation", holding the flushed partial text; the optimistic user bubble goes with the live turn |
| `error` `aborted` | `idle` | Nothing — sign-out or a route-change reset already cleared the state, and patching would resurrect a turn (for sign-out, the previous user's) |
| `error` `aborted` | any other phase | The live turn is cleared, nothing else — clearing cannot resurrect anything, but a phase left in flight would wedge the composer on a Stop control with nothing to stop |
| `error`, any other arm | `stopping` | Prompt reseeded into the composer, **no** notice — the user wanted out; raising an error card for a turn they cancelled would be noise |
| `error`, any other arm | `awaitingFirst` / `streaming` | Pre-stream failure notice, with Retry where the arm is retryable |
| Create `POST` failed (never reached the stream) | `creating` | Failure notice without Retry + prompt reseeded (§3.2) |

Three decisions behind the table:

- **`aborted` is discriminated by phase, never by abort reason.** All three abort sources — Stop, sign-out, unsubscribe — surface as the same `aborted` arm ([streaming client §4.2](streaming-client.md#42-aborting--stop-sign-out-unsubscribe)). What distinguishes them is what the store did *first*: Stop flips the phase to `stopping` before aborting, while sign-out and route changes reset to `idle` before the rejection lands.
- **`Finished` racing a Stop wins.** The abort raced a clean close: the server transcribed the answer, so a card claiming "not saved" would be false — US-407's own honesty rationale.
- **The settle is one atomic patch.** The optimistic user bubble is a *field* (`pendingUserText`), not a transcript entry — so `Finished` appends user and assistant entries in a single `patchState` (US-406's "exactly one append"), and Stop removes the bubble by nulling a field rather than popping an array.

A body that ends without `Finished` is US-406's deliberate **non-error** arm: a mid-stream fault and a network drop are indistinguishable on the wire ([contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame)), so the partial answer keeps its place in the transcript under a "This response was cut off" warning card, with a Retry that re-sends the turn's own prompt and selection — as sent, even if the pickers have moved since.

## 6. Stop

Stop (US-407) does different things depending on what there is to stop:

- **During `creating`** — the POST is cancelled and the prompt goes straight back into the composer. Nothing streamed, so there is no partial output to card.
- **During `awaitingFirst` or `streaming`** — the phase flips to `stopping` *before* the abort, for the settle discrimination above, and because the patch is synchronous the Send control returns immediately rather than after the rejection's round trip.

The result is the **detached stopped card** (frame `1h`): dashed border and left inset, the design's statement that this text is *not* part of the transcript — the server transcribed neither the answer nor the prompt, and the card vanishes on route change, reopen, and reload because the state behind it does. It offers:

- **Copy** — the partial text, with a two-second "Copied" confirmation (the focused button's label change is what announces it; a denied clipboard permission leaves the button as it was).
- **Retry** — restores the prompt into the composer *and* the turn's model and MCP selection into the pickers, through the same `applyConversationSettings` seam US-410 seeds a reopened conversation with (§7.6) — and deliberately does **not** re-send. The selection restore routes through the twice-enforced MCP gating invariant ([streaming client §5.3](streaming-client.md#53-the-mcp-gating-invariant-enforced-twice)), so a selection that has gone stale cannot produce the server's 400.

There is no stop endpoint and none is called: aborting the fetch is what ends the turn server-side, and the server still records what the stopped turn spent.

## 7. Reopening a conversation (US-410, US-409)

Until this story, opening an existing conversation showed its title over an empty screen: nothing in the client had ever read `GET api/conversations/{id}/messages`. US-410 was written as "restore the model and MCP selection" and sized S; its third criterion is about a *refetched transcript*, which presupposed a fetch that did not exist, so history replay shipped inside it and it was re-sized to M (agreed with the product owner before implementation, and recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)).

Two independent restores happen on open, in two stores:

| What is restored | From | Owned by |
|---|---|---|
| The stored messages | `GET api/conversations/{id}/messages` | `TurnStore` (§7.1–§7.5, §7.7) |
| The model and MCP selection | `GET api/conversations/{id}` | `ConversationStore` → `TurnSettingsStore` (§7.6) |

US-409 rides along at the other end of the same round trip — after the *first* turn completes, the conversation is re-read for the name the server generated from it (§7.8).

### 7.1 Replay hangs off the reset, not the route input

`loadHistory` is an `rxMethod` on `switchMap`, called from exactly two places: `bindRoute`'s reset path, and the failure panel's Retry. Every call therefore cancels the one before it, so a read for a conversation the user has already left can never install entries onto a screen they are no longer on.

Hanging it off the **reset** rather than off the route input is what keeps US-401's own `replaceUrl` out of it. That navigation re-enters `bindRoute` with the id the store just bound itself, which the guard returns early from as a no-op (§3.1) — and must, because the conversation it names has a first turn still streaming and therefore nothing persisted to read back. Reading there would replace a live transcript with an empty one.

While the read is out, `historyPending` is true: the transcript renders a two-block skeleton, `aria-busy` rides the container (the skeleton itself is `aria-hidden`, so without that the region would be silently empty and then silently full), and `hasContent` counts the pending state — otherwise a conversation being read back would show the landing screen's prompt chips for a round trip and then swap them for its own transcript.

### 7.2 A replayed answer goes through the same reducers as a live one

[`replayAssistantText`](../../enterprise-gpt-ui/src/app/domain/stream/replayed-turn.ts) does not hand-assemble a snapshot. It builds **one synthetic `TextDelta`** and folds it through both reducers a live turn uses — the vendored `foldAssistantEvents` and `foldTimeline`:

```typescript
export function replayAssistantText(text: string): ReplayedTurn {
  const event = textDeltaEvent(text);

  return {
    snapshot: foldAssistantEvents(createInitialSnapshot(), event),
    timeline: foldTimeline(createInitialTimeline(), event),
  };
}
```

The result is a `{ snapshot, timeline }` pair indistinguishable from a settled live turn's, so a replayed answer renders through the existing `AssistantTurn` — markdown, code highlighting and all — with **no second code path**, and cannot drift from the live one when the vendored contract is upgraded. `textDeltaEvent` now lives in [`stream-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/stream-codec.ts) and is shared with the raw-text fallback codec ([streaming client §3.3](streaming-client.md#33-the-raw-text-fallback--featuresrawstreamcodec)), which is what makes the two synthesized events byte-identical.

No `Completed` phase is invented for a replayed entry, and no `Finished` usage: nothing about a stored message says how its turn ended, so the snapshot is left at its initial phase.

### 7.3 Two id ranges, so "replaced, not merged" is safe

Transcript entry ids are the `@for … track` key, and they are split into two disjoint ranges:

| Range | Holds | Direction |
|---|---|---|
| `-1, -2, -3, …` | Replayed history | Descending, assigned in message order |
| `1, 2, 3, …` | Turns taken this session | Ascending |

That split is what lets a refetch **rebuild the whole history block** without renumbering — or colliding with — entries taken since, keeping their `@for` identity stable across the swap. US-410's third criterion is the reason a rebuild is needed at all: after an aborted turn the server holds *fewer* messages than the screen does, so a merge would leave the abandoned pair on screen forever, claiming the model saw them. A replace lets the transcript legitimately *shrink*.

The two callers disagree about one thing, `keepLive`:

- **A route-change read keeps live entries.** It was issued before any turn taken during it, so those entries are still to come and are not in the response.
- **The failure panel's Retry discards them.** It is issued *after* everything on screen — the panel deliberately leaves the composer usable — so a turn taken while history was broken is already in the response about to be installed, and keeping it would render the exchange twice.

### 7.4 The message shape has two sharp edges

```jsonc
// GET api/conversations/{id}/messages
{ "id": "…", "name": "…", "messages": [ { "text": "…", "role": 3 } ] }
```

- **`role` is a number, not a string.** The API registers no `JsonStringEnumConverter`, so `ChatRoles` serializes as its integer value: `1` System, `2` Assistant, `3` User, `4` Tool. [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts) mirrors that as `CHAT_ROLE`. Only `user` and `assistant` are rendered — the server already filters `System` out of the response, `Tool` is a model-to-model artefact whose proper surface is the activity timeline this response cannot reconstruct anyway, and an unknown role from a future server is skipped rather than guessed at.
- **Only `messages` may be read from the response.** It is `ConversationDto`-shaped on the wire but is rebuilt from the Cosmos transcript document, which reports `projectId`, `modelId` and `isFavorite` as `null` / `null` / `false` whatever the truth ([shell §3.2](../ui/shell-and-navigation.md#32-get-apiconversationsid-versus-idmessages)). The client types it as its own `ChatConversationDto` with three fields rather than extending `ConversationDetailDto`, so that trap is unreachable through the type.

A message carries no id, no timestamp and no usage; US-1101 is the story that would add them.

### 7.5 Replayed history carries no activity timeline — permanently

The stream carries tool, MCP, agent and reasoning activity live and transcribes **only the answer text** ([contract §6.2](streaming-contract.md#62-only-the-answer-is-transcribed)). So a replayed turn is answer text and nothing else: no activity cards, no durations, no usage. A turn taken in the current session keeps its full timeline until the screen is left, and loses it on reload by the same fact.

This is recorded as an interim-behaviour row in the [build order](../prd/enterprise-ui-rebuild-build-order.md) with **nothing planned against it** — closing it would need a server-side turn record, which is a new backend enabler, not a client story. Anything a user must keep from a stopped or cut-off turn still goes through the stopped card's Copy (§6); nothing else preserves it.

Any change of conversation — another sidebar row, "New conversation", a deep link — still aborts a live turn and clears the transcript, the stopped card, and every notice before the replay starts. Sign-out does the same through `withResetOnSignOut` plus an explicit abort, and `loadHistory` composes `takeUntil(signedOut$)` for the repo's standing reason: clearing state is not cancelling work.

### 7.6 Restoring the model and MCP selection

`ConversationStore` seeds `TurnSettingsStore.applyConversationSettings({ modelId, mcpServerIds })` from the `GET api/conversations/{id}` response — the only shape carrying `mcpServerIds`. The seed is deliberately **unvalidated**: `selectedModel` falls back to the catalog default for an id that is gone, and `effectiveMcpServerIds` re-intersects with the permitted catalog, so a stale selection structurally cannot reach the wire ([streaming client §5.3](streaming-client.md#53-the-mcp-gating-invariant-enforced-twice)).

Two things in it are easy to get wrong.

**An empty payload is not a selection of "nothing".** The server writes `modelId` and the MCP set from the newest `ConversationUsage` row, so a conversation whose first turn has **not completed** reports `modelId: null` and `mcpServerIds: []` — which is exactly the state US-401's `replaceUrl` re-opens this store in, while that first turn is still streaming. Seeding it would clear the model and tool servers the user picked for the turn running right now: US-402 and US-403 undone by a round trip. The seed is therefore skipped when both are empty.

**A deactivated model has to be reported, not absorbed.** `restoredModelId` is remembered separately from the selection so the fallback to the default can be explained rather than happening silently; `restoredModelUnavailable` then gates on the catalog having actually *resolved*, because every deep link races the catalog load and an empty `models()` would otherwise flash "your model was deactivated" at every conversation and then withdraw it. The composer renders the result as an amber note above the control row — a state no design frame draws, so it reuses frames `2d`/`2j`'s treatment (§9.1). Picking a model clears `restoredModelId`: the user has answered the warning. Opening the empty screen clears it too, through `clearRestoredModel()` — there is nothing resumed there to explain — while the selection itself persists across conversations for the session, as US-402 requires.

### 7.7 When the stored transcript cannot be read

`historyError` renders the shared `ErrorPanel` with the heading "Earlier messages couldn't load", the `traceId`, and a Retry offered only where `canRetry` says one could work (§8.3). **The composer stays live beneath it**: failing to read the past does not stop a user taking a new turn, it only means they cannot see what came before. That is also why the Retry discards live entries (§7.3).

### 7.8 The name the server generates (US-409)

The server names a conversation from its first prompt and says so **nowhere on the stream**, so a refetch is the only way to learn it. [`turnEvents`](../../enterprise-gpt-ui/src/app/core/events/turn-events.ts) is the app's third event group (after `sessionEvents` and `conversationEvents`) and carries one event:

```typescript
turnEvents.completed({ conversationId, wasFirstTurn });
```

The Events plugin for the same reachability reason as `conversationEvents`: `TurnStore` is route-scoped and knows nothing about the sidebar, while `ConversationListStore` is root-scoped and cannot reach into a route-scoped store to be called by it ([Frontend Foundation §5.6](../ui/frontend-foundation.md#56-cross-store-events)).

Three details decide when it fires and what it says:

- **Only a `Finished` frame counts.** The dispatch is in the stream's `complete` callback and guarded on `snapshot.phase === 'Completed'`, so a stopped, cut-off or failed turn publishes nothing — the server named nothing after it, and an observer refetching would read back the placeholder it already holds. That is US-409's third criterion with no special case: the retry that actually completes is the one that announces.
- **`wasFirstTurn` is read *before* the settle** appends the entry that would make this turn look like it had a predecessor. It derives from "the transcript holds no `assistant` entry", which is why a cut-off attempt — which leaves a `cutOff` entry, never an `assistant` one — still counts the next attempt as first, while §7's replayed history correctly marks an existing conversation as not first.
- **An unread history is not an empty one.** The flag is false when `historyError` is set, or the client would refetch a name the server settled long ago.

`ConversationStore` handles the event with a **silent** `refreshDetail`, not `open` or `retry`: those go through `setPending()` and would blank the header mid-conversation, flashing the sidebar's stale name back for a round trip. Its failure is swallowed — everything on screen is still true, and a name arriving one refresh late does not deserve an error panel — and the swallow lives inside `tapResponse` rather than a `catchError` ahead of it, because a throw escaping there would kill the `rxMethod` and every later first turn would silently stop refreshing. The sidebar needed no new code: `refreshDetail` calls the same `ConversationListStore.refreshRow` seam the detail load already used, unguarded by the open conversation id, so the row earns its generated name even if the user has moved on mid-round-trip.

## 8. Rendering the turn

[`Transcript`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts) is the conversation column: the history skeleton or its failure panel first (§7.1, §7.7), then the settled entries, then whichever live region applies — the thinking ridgeline, the streaming turn, a failure notice, or the stopped card. Its accessibility posture is deliberate:

- `aria-busy` rides the container for the length of a turn **and while the stored messages load**, so assistive tech treats the region as settling rather than announcing every re-render. The full streaming live-region treatment is US-1402's.
- A **persistent** visually-hidden `role="status"` region announces abnormal endings only — stopped, failed, cut off. It exists from the first render because a live region created together with its content is not reliably announced, and it empties during every turn so a retry that cuts off *again* transitions the content and is announced a second time.

[`AssistantTurn`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts) renders the avatar column and, in arrival order, the turn's text blocks and activity cards — the render-time join of §4. The blinking caret belongs to the last node, and only when it is a text block. Text renders as **sanitized markdown** as of US-601, and the node still growing renders as two `<markdown>` instances — a stable head and a volatile tail (US-602). Neither the pipeline nor the split is restated here: see [Answer Rendering](../ui/answer-rendering.md). Two facts from it change how this screen behaves, though — the renderer writes its output a microtask *after* change detection returns, which is why US-606's scroll pinning is driven by a `ResizeObserver` rather than by these signals; and the caret is now a CSS pseudo-element on the rendered block, because `innerHTML` leaves no template position inside it for a span.

[`ActivityCard`](../../enterprise-gpt-ui/src/app/features/chat/transcript/activity-card.ts) is frame `1f`: the activity's `displayName` as the label, the kind as a **separate** badge — the contract keeps them apart so no card ever reads "Calling Jira Cloud MCP" — the `source` subtitle verbatim (the "Atlassian · host" composition is the server's, arriving as one string), sub-status bullets, and a running ring / completed check / failed triangle with durations in mono (`3.1s`, absent until completion). No chevron and no expansion yet — that affordance arrives with US-504's usage strip. [`KindBadge`](../../enterprise-gpt-ui/src/app/shared/badge/kind-badge/kind-badge.ts) is pinned to exactly four labels, one per `ToolKind` arm — Function, MCP tool, Agent, and **Reasoning** for `Unknown`, the kind reasoning phases stream under (previously mislabelled "Tool"; US-501 completed the set).

[`TurnNoticeCard`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-notice-card.ts) carries every turn-edge notice, and now has four designed arms plus a fallback:

| Arm | Frame `1h` variant | Story |
|---|---|---|
| Cut off | Warn panel, triangle, Retry | US-406 |
| Tool server unavailable | Plain `--surface` card, plug icon, names the server | US-403 / US-406 |
| Conversation busy | Warn panel, **one row** — hourglass, one sentence, Retry pushed right | US-408 (§8.1) |
| MCP authorization required | Plain `--surface` card, `--warn` shield, **no action** | US-412 (§8.2) |
| Anything else | Generic warning with the `userMessage` and the `traceId` line | — |

The fallback is deliberate rather than lazy: an arm with no designed frame gets the honest generic treatment instead of invented copy. The card gained a `layout` axis (`stack` | `row`) for the busy arm, which is the only one-line variant the design draws.

### 8.1 The busy warning (US-408)

Most of this story was already structural. `settlePreStream` lands the typed 409 in `turnError` with its retry snapshot, `canRetry` says yes for `conversation-busy`, and `isTransientRetriable` says no to every problem-typed arm — so "no automatic retry at any interval" holds because nothing in the client can schedule one, not because a scheduler was omitted. What shipped is the frame: `--warn-bg` panel, `--warn-border`, hourglass, the sentence "Another response is still running in this conversation.", and Retry on the far right. **No spinner** — nothing is being awaited — and **no trace id**, because a second tab holding the conversation lock is not a defect to correlate.

The copy is hard-coded beside the tool-server arm's rather than taken from `userMessage`, which has to say "This conversation…" for a toast that could fire from anywhere; a card sitting directly under the prompt that caused it can say "another response" and be clearer for it.

**One accessibility defect was found in review and fixed here**, and it is the reason `TurnNoticeCard` exposes a `focusRetry()` method. Retrying destroys the button that was pressed — the send clears the notice — so focus falls to `<body>` for the length of the attempt; the composer's settle fixup (§9) then claimed it for an empty prompt box, and a keyboard user re-traversed the page on **every** attempt of exactly the loop this story is about. Two changes close it: `Transcript` hands focus to the replacement Retry when focus has fallen through, and the composer's fixup stands down while a notice is on screen, because the prompt a retry re-sends lives in the notice rather than in the box. Both are conditioned on "focus is nowhere" — `null`, `<body>`, or a detached element — never on "the user retried", so a notice raised while the user is somewhere real never steals focus from them.

### 8.2 The MCP consent card (US-412)

A 403 `/problems/mcp-authorization-required` renders frame `1h`'s **current** consent variant: the plain `--surface` card, a `--warn` shield, the title "*{serverName}* requires authorization" in body colour, and copy stating that no authorization flow is available yet and to contact an administrator. No trace line — there is nothing to correlate, and the copy already names what an administrator must fix.

**There is no consent action, and the future variant is deliberately not built.** `scope` is null on every 403 this build can receive — `McpDto` omits it and only the administrative `McpServerDto` carries it — so an "Authorize *{serverName}*" button would have nothing to request. It arrives with **US-411**, the enabler that puts the scope on the wire. That deferral is recorded in the build order.

Retry is withheld by `canRetry`, not by the card: `mcp-authorization-required` is `false` in `RETRYABLE_KINDS` because consent happens elsewhere and re-sending the same turn cannot supply it. Review found that table had **no test anywhere**; it is now pinned in both directions, since it is the single fact keeping this card from growing a dead button and US-408's from losing its live one.

Criterion 1 — no token refresh, no refresh loop — was already structural: `authErrorDecision` refreshes only the bare-401 `http` arm ([Frontend Foundation §4.5](../ui/frontend-foundation.md#45-autherrordecision--the-only-place-refresh-is-decided)), so this 403 cannot enter the refresh path however it is handled. It now carries a chat-level regression test beside the policy-level one.

**`userMessage` changed for this arm.** It said the tool server needs "*your* authorization", which is false — consent is an administrative act here and there is no flow to send the user to — and it is the only channel a screen-reader user gets, since the card is not a live region. It now reads `The tool server "X" requires authorization. Contact your administrator.` and agrees with the card.

### 8.3 Who decides whether Retry appears

Three layers, each answering a different question, and they must not be collapsed:

| Question | Answered by |
|---|---|
| Does a re-sendable turn exist at all? | `Transcript`, from the store's retry snapshot — passed to the card as `retryable` |
| Could a person pressing Retry plausibly get past this failure? | `canRetry` ([shell §8](../ui/shell-and-navigation.md#8-canretry--whether-to-offer-the-control-at-all)) |
| May the interceptor replay this automatically? | `isTransientRetriable` — never for a 409, never for a problem-typed 503 |

The card itself decides none of them, which is why the consent arm shows no action even though the store kept a retry snapshot.

## 9. The composer and the empty state

The Send and Stop controls are **one persistent button** that morphs with `inFlight` — the element survives the swap, so keyboard focus does too. One settle case still strands focus: a turn settling into an empty composer re-disables the morphed Send while it is focused, and engines disagree about what happens next (some drop focus to `<body>`, others leave it on the disabled control) — both arms are caught, and focus is handed to the textarea. Only a fall-through is corrected; a user focused elsewhere is left alone, and since US-408 the fixup also stands down when the turn settled into a notice, whose Retry is the thing worth focusing (§8.1).

Composer text seeding flows **one way, through the store**: `seedComposer` writes a one-shot seed (a fresh object per seed, so seeding the same text twice still propagates), the composer applies it, focuses the textarea, and acknowledges through `consumeComposerSeed`. Three producers use it — the suggested prompt chips, Stop-during-create restoring the prompt, and the Retry restores — and `protectedState` stays intact throughout. Dictated phrases use the same one-way handoff but a different signal (§9.2), because they *append* rather than replace.

### 9.1 The amber note is one message at a time

The line above the control row shows at most one message, ordered by what it costs the user not to know:

1. **The model catalog failed** (frame `2j`) — with no model there will be no turn, which makes everything below it moot.
2. **The restored model is unavailable** (US-410, §7.6) — it explains a selection the user did not make, and changes what the next turn costs.
3. **Tools were cleared by the model choice** (frame `2d`) — it explains a selection they did make.
4. **The microphone was blocked** (US-413) — it explains a control that failed.

The microphone ranks last on purpose: one denied permission must not hide the fact that the conversation is now answering on a different model.

### 9.2 Dictation (US-413)

Speaking a prompt runs on the browser's own Web Speech API. **Enterprise GPT never receives the audio**: no microphone stream reaches this application's code, nothing is uploaded to the API, and there is no server-side transcription service anywhere in the product — the PRD's "voice input" was scoped to browser-native recognition for exactly that reason.

That is a statement about *this* application, not about the browser. Several engines — Chrome's included — perform recognition remotely, sending the captured audio to the browser vendor's own speech service, so a regulated tenant should evaluate the browser's behaviour rather than assuming the audio stayed on the machine. Nothing in the client can change that; what the client controls is that it neither handles nor stores the audio itself.

**Absence, not disablement, where it is unsupported.** [`SPEECH_RECOGNITION`](../../enterprise-gpt-ui/src/app/core/speech/speech-recognition.ts) resolves `SpeechRecognition ?? webkitSpeechRecognition` and returns **null** where neither exists — Firefox today, and any non-secure origin. The composer asks the token whether to draw the control at all, so "absent rather than present-and-disabled" is a rendering consequence rather than a check someone has to remember. It is a DI token for the same reason as `STREAM_FETCH`: specs substitute a fake through a provider override instead of assigning to `window`, which is necessary here because the real interface exists in no test environment.

**The types are declared locally, on purpose.** TypeScript 5.9's `lib.dom.d.ts` ships `SpeechRecognitionResult`, `SpeechRecognitionResultList` and `SpeechRecognitionAlternative` but **not** `SpeechRecognition` itself, its event types, or the prefixed constructor. The file declares the four members US-413 uses and nothing more, rather than adding a dependency for four lines of types.

[`DictationStore`](../../enterprise-gpt-ui/src/app/features/chat/composer/dictation-store.ts) is **component-provided** by `Composer`, not root: a recording session belongs to the prompt box that started it, and its `onDestroy` aborts — nothing else releases the microphone, and the engine holds the device until told otherwise. Five behaviours are worth knowing:

- **`interimResults` stays off**, against the spec's own default. Interim guesses rewrite themselves word by word, and a textarea the user may also be typing in is the wrong place to watch that happen. Phrases land once they are final, which is also the only form that survives being edited afterwards.
- **Phrases append.** Speaking after typing, or after an earlier phrase, extends the prompt. The store holds one finalized phrase at a time in `spoken` (a fresh object per phrase, so the same words twice still propagate) and the composer consumes it, exactly as it consumes `composerSeed`.
- **Stopping uses `stop()`, never `abort()`**, so the engine emits the last phrase it heard. `abort()` is reserved for teardown, where the handlers are detached first so a destroyed store is not patched.
- **A denied microphone is explained, and the composer stays usable.** `not-allowed` and `service-not-allowed` set `permissionDenied`, which raises the note in §9.1; the flag is held until the next attempt rather than cleared immediately, because a browser told to block will not prompt again and the message has to outlive the click that earned it. Every other error — no speech, no network, an engine failure — ends the session silently: a message about `audio-capture` explains nothing a user can act on.
- **The control is one morphing button**, like Send/Stop. Frame `2i` puts the recording pill in the microphone's place — `rgba(33,168,216,.14)`, `ringpulse 1.8s ease-out infinite` (one of the design's four existing keyframes; no fifth was added), a filled mic glyph and the elapsed `m:ss` in JetBrains Mono — and morphing rather than swapping is what keeps focus on the control the user just pressed. The changing `aria-label` ("Dictate" → "Stop dictating") on that still-focused button is also what announces that recording started.

While a turn streams, **starting** is disabled and **stopping** is not: a user must always be able to release the microphone. The idle microphone carries `composer__aux`, so US-407's in-flight dimming applies to it and that rule finally matches a control; the recording pill deliberately does not, because a dimmed control that still works reads as broken.

The empty state (frame `1a`) closes US-303's deferral: the four suggested prompt chips — *Summarize a document*, *Draft a status update*, *Find open Jira blockers*, *Explain a code change* — render on the landing screen only, and a chip's entire behaviour is seeding the composer's text. No focus is moved on arrival, because this is the application's landing screen rather than a state transition, and taking focus would jump a keyboard user past the shell's skip link on every cold start.

## 10. Testing

`TurnStore`'s specs drive the send pipeline with a substituted `ConversationStreamClient` and settle every row of §5's table, including the races — `Finished` against Stop, a route change against a live create, a batch against a reset. The timeline reducer is framework-free and tests in Node beside the codecs ([`turn-timeline.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.spec.ts)); the transcript and composer specs cover the join, the caret, the announcements, and the focus fixups.

EP-4's closing five added four seams to that:

- **Replay** is framework-free too — [`replayed-turn.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/replayed-turn.spec.ts) runs in Node, and `TurnStore`'s specs cover the id ranges, both `keepLive` arms, the shrinking transcript, and the read cancelled by a route change.
- **`turnEvents.completed`** is asserted on both sides: dispatched only on `Finished` and with the right `wasFirstTurn`, and handled by `ConversationStore` without going pending.
- **The `RETRYABLE_KINDS` table** is pinned in both directions, which is what keeps the consent card actionless and the busy card actionable.
- **Dictation** substitutes `SPEECH_RECOGNITION` by provider override with the fake in [`@testing/speech`](../../enterprise-gpt-ui/src/testing/speech.ts) — the real interface exists in no test environment, so there is nothing else to drive it with.

The epic closed with the suite at **961 specs**, all green, and `npm run lint`, `npm run format` and `npm run build` clean. The initial bundle is **unchanged** at 660.25 kB raw / 160.99 kB transfer (665 kB warn / 720 kB fail): everything here rides the lazy `chat` chunk, which grew 179.47 → 187.59 kB.

## 11. Key files

| Concern | File |
|---|---|
| Turn store — phases, transcript, settles, history replay | [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) |
| Arrival-order index | [`domain/stream/turn-timeline.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.ts) |
| Replaying a stored answer, and the shared synthetic event | [`domain/stream/replayed-turn.ts`](../../enterprise-gpt-ui/src/app/domain/stream/replayed-turn.ts), [`domain/stream/stream-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/stream-codec.ts) |
| Stored-message shape and roles | [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts) |
| Turn completion event | [`core/events/turn-events.ts`](../../enterprise-gpt-ui/src/app/core/events/turn-events.ts) |
| Conversation detail, settings seed, silent name refresh | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts) |
| Transcript container, live announcements | [`features/chat/transcript/transcript.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts) |
| Assistant turn — the render-time join | [`features/chat/transcript/assistant-turn.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts) |
| Activity card, kind badge | [`features/chat/transcript/activity-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/activity-card.ts), [`shared/badge/kind-badge/kind-badge.ts`](../../enterprise-gpt-ui/src/app/shared/badge/kind-badge/kind-badge.ts) |
| Turn-edge notices | [`features/chat/transcript/turn-notice-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-notice-card.ts) |
| Stopped card | [`features/chat/transcript/stopped-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/stopped-card.ts) |
| Composer — morphing button, seeding, the amber note | [`features/chat/composer/composer.ts`](../../enterprise-gpt-ui/src/app/features/chat/composer/composer.ts) |
| Dictation — token and session store | [`core/speech/speech-recognition.ts`](../../enterprise-gpt-ui/src/app/core/speech/speech-recognition.ts), [`features/chat/composer/dictation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/composer/dictation-store.ts) |
| Turn settings — restored model, gating invariant | [`core/chat/turn-settings-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/turn-settings-store.ts) |
| Empty state and chips | [`features/chat/chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts) |
| Chat screen — providers, route binding | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts) |
| Markdown rendering, the streaming split, scroll pinning | [`features/chat/markdown/`](../../enterprise-gpt-ui/src/app/features/chat/markdown/), [`domain/markdown/streaming-split.ts`](../../enterprise-gpt-ui/src/app/domain/markdown/streaming-split.ts), [`features/chat/transcript-pinning.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts) |
| Related reference | [Conversation Streaming Contract](streaming-contract.md), [Conversation Streaming Client](streaming-client.md), [Answer Rendering](../ui/answer-rendering.md), [Shell & Navigation](../ui/shell-and-navigation.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
