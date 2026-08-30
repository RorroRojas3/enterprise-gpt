# Conversation Turn Lifecycle

The layer above the [Conversation Streaming Client](streaming-client.md): what happens between pressing Send and a settled transcript entry, and what happens when a conversation is reopened. Shipped by thirteen stories: US-401 (send a first prompt; the conversation is created _around_ it), US-406 (streamed answer rendering), US-407 (Stop, keeping the partial output) and US-501 (the chronological activity timeline) built the live turn; EP-4's closing five then finished it — **US-408** (the busy warning, §8.1), **US-412** (the MCP consent card — removed 2026-08-22, superseded by US-414/US-415, §8.2), **US-410** (reopening a conversation: its stored messages _and_ the settings it last used, §7), **US-409** (the name the server generates after a first turn, §7.8) and **US-413** (dictation, §9.2). EP-5's closing four then finished the turn's _own_ surfaces — **US-502** (nested activity cards, §8.4), **US-505** (a failed activity, §8.4), **US-504** (what a turn cost, §8.5) and **US-503** (the reasoning region, §4.2 and §8.6). Four recorded deferrals closed along the way: US-303's suggested prompt chips, US-403's tool-server-unavailable card, the generic notice standing in for the busy and consent frames, and US-407's `composer__aux` dimming rule, which had matched no control until the microphone arrived.

Audience: whoever builds the stories on top — response rating (US-1103), the streaming live region (US-1402) — or debugs a turn that ended strangely. The wire format lives in the [contract](streaming-contract.md); the codec, transport, and turn settings live in the [streaming client](streaming-client.md); how the answer text becomes HTML, how a message or a code block is copied, and how the page follows the newest text all live in [Answer Rendering](../ui/answer-rendering.md) — none of the three is restated here.

## 1. Overview

One route-scoped store owns everything: [`TurnStore`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) holds the session transcript, the live turn, and the send flow, provided by `Chat` beside `ConversationStore` — so its lifetime is the chat screen's, and leaving `/chat` unsubscribes the stream, which aborts the fetch (there is no stop endpoint; [streaming client §4.2](streaming-client.md#42-aborting--stop-sign-out-unsubscribe)).

| Concern                    | What it does                                                                                          | Where                                                                            |
| -------------------------- | ----------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| Turn state                 | Phases, transcript entries, live snapshot + timeline + reasoning window, settle logic, history replay | `features/chat/turn-store.ts`                                                    |
| Arrival-order index        | Which text blocks and activity cards arrived in what order                                            | `domain/stream/turn-timeline.ts` — framework-free, beside the vendored fold      |
| Reasoning window           | Where the model's own reasoning starts, and how long it ran                                           | `domain/stream/reasoning-timing.ts` — framework-free (§4.2)                      |
| Replaying a stored answer  | One persisted message → the same folded shape a live turn produces                                    | `domain/stream/replayed-turn.ts`                                                 |
| Transcript rendering       | Settled entries, the live turn, the notices, the stopped card, the history states                     | `features/chat/transcript/`                                                      |
| Token counts and durations | The one formatter behind the message footer and the activity strip                                    | `features/chat/transcript/turn-usage.ts` (§8.5)                                  |
| Send / Stop control        | The morphing button, composer seeding, focus recovery                                                 | `features/chat/composer/`                                                        |
| Dictation                  | The Web Speech session, the elapsed clock, the phrase handoff                                         | `core/speech/speech-recognition.ts`, `features/chat/composer/dictation-store.ts` |
| "A turn completed"         | The one fact this screen publishes to the rest of the app                                             | `core/events/turn-events.ts`                                                     |
| Landing screen             | The wordmark, prompt heading, and four suggested prompt chips                                         | `features/chat/chat-empty-state.ts`                                              |

Three structures describe one live turn, and none duplicates the others (§4). Reopening a conversation replays its stored messages (§7) — but only the messages: the **activity timeline, the reasoning summary and the token counts are session state**, because the stream that produces them is never persisted, and that is permanent rather than pending (§7.5).

## 2. The turn state machine

`TurnPhase` is one value, and everything the screen shows during a turn derives from it:

| Phase           | Meaning                                                   | What shows                                                                       |
| --------------- | --------------------------------------------------------- | -------------------------------------------------------------------------------- |
| `idle`          | No turn in flight                                         | The Send control; transcript or empty state                                      |
| `creating`      | `POST api/conversations` is out for a first prompt        | The thinking ridgeline (frame `1e`)                                              |
| `awaitingFirst` | The stream request is out; nothing renderable has arrived | The thinking ridgeline — or the reasoning region, once the model reasons (below) |
| `streaming`     | Content is arriving                                       | The live turn, with the blinking caret                                           |
| `stopping`      | The user pressed Stop; the abort's error is on its way    | The live turn, until the settle lands                                            |

Three computed signals read it, and the first two no longer split on the phase alone. `inFlight` is anything but `idle` — which is also what morphs the composer's button into Stop (§9). `showThinking` and `showLiveTurn` split on **"is the model reasoning"**, so that during `awaitingFirst` exactly one of them is true:

|                | `creating` | `awaitingFirst`, no model reasoning | `awaitingFirst`, model reasoning | `streaming` / `stopping` |
| -------------- | ---------- | ----------------------------------- | -------------------------------- | ------------------------ |
| `showThinking` | ✅         | ✅                                  | —                                | —                        |
| `showLiveTurn` | —          | —                                   | ✅                               | ✅                       |

That split is US-503's (§8.6). The pre-answer gap belongs to the ridgeline on every turn where the model reasons silently, which is most of them, and is handed to the reasoning region the moment the model reasons in its own words — so the summary is readable _as it streams_ rather than appearing whole at the first token, in one component instance for the length of the turn.

Reading a conversation's stored messages is deliberately **not** a phase (`historyPending` / `historyError` are their own state, §7.1): no turn is in flight while history loads, the composer stays usable throughout, and folding it into `TurnPhase` would have put both facts at risk.

`awaitingFirst` advances to `streaming` only when a batch carries **renderable** content — an `ActivityStarted` or a `TextDelta`. A batch of `Status` or `ReasoningDelta` events deliberately does not move the phase. US-406 wrote that rule when this app emitted no request-level statuses at all; that rationale is now historical — the server opens every turn with a synthetic `Status` (`"Starting"`) and a server-authored `ReasoningDelta` ([contract §4.3](streaming-contract.md#43-a-turn-frame-by-frame)) — but the rule now does real work: without it, those two frames would advance the phase with nothing renderable to show for it.

The client still renders neither `assistantStatus` nor the server's seeded reasoning delta, and both refusals are the same one: US-406 decided this app shows no request-level status line it did not get from the model. What US-503 added is the _model's_ reasoning, which is neither fabricated nor a status line — and reaching it means measuring the seed out first (§4.2).

## 3. Sending, and creating the conversation around the first prompt

The send pipeline is one `rxMethod` on `exhaustMap` — the double-send guard. A send while a turn is in flight is _ignored_, not queued: a queued one would only meet the server's 409, because a conversation runs one turn at a time ([contract §6.3](streaming-contract.md#63-it-is-not-resumable-and-not-multiplexed)). `send(prompt)` also re-checks what the composer's disabled button already enforces — no selection resolved or empty text is a no-op, so no caller can issue a turn without a `modelId`.

### 3.1 The unbound send

"New conversation" creates nothing — the empty screen is just a screen, and no conversation exists until the first prompt is sent (US-401, US-302). When a send finds no `boundConversationId`, the store:

1. Enters `creating` and POSTs `api/conversations` with `{ "projectId": null }`.
2. On the 201: prepends the new conversation into `ConversationListStore` (so the sidebar row and the header's list-fallback name exist the moment the URL updates), patches `boundConversationId`, **then** navigates.
3. Navigates with `router.navigateByUrl('/chat/{id}', { replaceUrl: true })` and streams the turn against the new id.

The navigation is a **recorded deviation from US-401's letter** (product-owner approved, 2026-08-12). The story says `Location.replaceState`, but that moves the URL without moving the Router: the sidebar's `routerLinkActive` selection breaks, "New conversation" becomes a same-URL no-op, and the route's `conversationId` input never binds. `replaceUrl: true` keeps the story's _intent_ — history replaced, no remount, because one route configuration serves both `/chat` and `/chat/{id}`.

The ordering in step 2 is load-bearing. `bindRoute` follows the route's `conversationId` input and resets everything on a change of conversation — but it treats an incoming id equal to `boundConversationId` as "the URL caught up with our own create" and passes it through as a no-op. Setting the id _before_ navigating is what makes the store's own navigation indistinguishable from no change at all.

### 3.2 When the create goes wrong

- **The POST fails** — the prompt goes straight back into the composer (US-401's "not lost") and a failure notice renders _without_ a Retry button, because the reseeded composer is the retry surface. The pipeline returns `EMPTY` so the `rxMethod` stays alive for the next send.
- **The user presses Stop, or navigates away, mid-create** — the POST is cancelled (it accepts no `AbortSignal`, so a `takeUntil` subject cancels it) and the prompt is reseeded. A route event during `creating` is always foreign — the store's own navigation cannot fire before the 201 has moved the phase on — so `bindRoute` cancels the create before resetting; without that, the 201 would land after the reset, clobber the new binding, and navigate the user off the conversation they just opened.
- **Sign-out mid-create** — the same `takeUntil` races `injectSignedOut()`, and `withResetOnSignOut` (composed last, per the repo rule) clears the state.

## 4. Folding a live turn: the snapshot, the timeline index, and the reasoning window

Each delivered batch — at most one per ~16 ms flush, at any token rate ([streaming client §4.4](streaming-client.md#44-batching)) — folds through **three** reducers in **one** `patchState`:

| Reducer                                                                                    | Produces                  | Owns                                                                        |
| ------------------------------------------------------------------------------------------ | ------------------------- | --------------------------------------------------------------------------- |
| `foldAssistantEvents` (vendored)                                                           | `AssistantStatusSnapshot` | Activity state and hierarchy, answer text, reasoning text, usage, phase     |
| [`foldTimeline`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.ts)           | `TurnTimeline`            | Arrival order, and nothing else (§4.1)                                      |
| [`foldReasoningTiming`](../../enterprise-gpt-ui/src/app/domain/stream/reasoning-timing.ts) | `ReasoningTiming`         | Where the model's reasoning starts in that text, and how long it ran (§4.2) |

All three are pure and framework-free, and all three test in Node. The apply is guarded to in-flight phases, so a batch racing a reset cannot resurrect a cleared turn; batches landing while `stopping` still fold, because they are the partial output the stopped card keeps (§6).

### 4.1 Why an index beside the fold

`foldAssistantEvents` owns every piece of activity _state_ — labels, kinds, progress, completion — but its snapshot is hierarchical and unordered across text: it can say a tool ran and finished, but not whether its card arrived before or after a paragraph. US-501 needs exactly that interleaving, so `TurnTimeline` records **only** the ordering:

- A `text` node is a half-open `[start, end)` slice into the folded snapshot's single `text` string. The timeline never copies answer text, so a delta costs O(1) here regardless of transcript length; consecutive deltas extend the last text node rather than opening a new one.
- An `activity` node is a `scopeId` reference into the snapshot's activity tree. Every `ActivityStarted` becomes its own node **regardless of depth** — which is exactly what let US-502 nest children without touching this reducer (§8.4): the renderer keeps the nodes whose scope is a root of the folded snapshot and leaves the rest to the parent cards they already sit inside.

No structure duplicates another — the timeline holds no text and no activity state, the snapshot holds no ordering, and the reasoning window holds no text at all, only two clock readings and a length. The timeline applies the same `?? ''` fallbacks as the vendored fold, so offsets always index `snapshot.text` and the render-time join cannot miss on an event the fold accepted.

The join happens once per flush in `AssistantTurn`'s `renderedNodes` computed — not per node in the template — as slices of `snapshot.text` for text nodes and root matches for activity nodes. `findActivity` is **gone**: it walked the tree to any depth, which is the wrong question now that a nested activity is drawn by its parent rather than by the timeline.

### 4.2 The reasoning window, and the seed that is not the model's

Two facts about the contract shape US-503, and the first is easy to miss.

**Every turn's first `ReasoningDelta` is server-authored.** The service seeds `"Reviewing the request and preparing a response.\n\n"` ahead of the model's first event ([contract §4.1](streaming-contract.md#41-event-kinds), [§4.3](streaming-contract.md#43-a-turn-frame-by-frame)), and the vendored fold accumulates it into `reasoningText` like any other delta. Rendering it would put a request-level status line on screen — the thing US-406 decided this app does not do — and _measuring_ from it would report queue and network latency as thinking time.

So the seed is identified **by position, not by its text**: `foldReasoningTiming` records the length of the first `ReasoningDelta` it sees and nothing about its content, and `modelReasoningText` slices that many characters off the front of `snapshot.reasoningText`. Hard-coding the server's copy in the client would be the brittle way to reach the same place — a wording change on the server would resurrect the status line.

**The second fact is that no reasoning duration is on the wire.** `ReasoningDelta` leaves `timestamp` at its default ([contract §4.2](streaming-contract.md#42-fields)) and the `durationSeconds` on `Finished` is the whole turn's, which the fold discards. The window is therefore measured from arrival times — which is what a reader is watching anyway:

|                  | Event                                                                |
| ---------------- | -------------------------------------------------------------------- |
| Opens the window | The **model's** first `ReasoningDelta` — the second one of the turn  |
| Closes it        | The first `TextDelta` or `ActivityStarted` after that, or `Finished` |

Three consequences fall out of that shape:

- **A turn whose only reasoning is the seed reports nothing and renders no region at all** — the same rule as an unreported token count (§8.5), and what makes US-503's third criterion true in practice rather than only for replayed history.
- **Reasoning that resumes mid-answer neither restarts nor extends the window.** What the pill reports is how long the model thought _before_ it started answering, and a model reasoning again has already answered.
- **`Finished` closes an open window**, so a turn that reasoned and then produced nothing still reports its seconds rather than losing its only figure.

The clock is a **parameter**, not something the reducer reads, which is what keeps it pure and lets a spec pin a turn's seconds without owning a fake clock. `TurnStore` passes `performance.now()` — deliberately not `Date.now()`, because this reading is one end of an elapsed measurement and a clock step between the two ends would render a negative duration. One reading per flush is enough: a ~16 ms batch is finer than a duration written to a tenth of a second.

## 5. Settling: how a turn ends

The transport ends a turn in exactly two ways — `complete`, or `error(AppError)` ([streaming client §4](streaming-client.md#4-the-transport)) — and the store discriminates each against the phase and the folded snapshot. This table is the heart of US-406 and US-407:

| Stream ending                                                        | Phase at settle                                | Outcome                                                                                                                                                                 |
| -------------------------------------------------------------------- | ---------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `complete`, `Finished` was folded (`snapshot.phase === 'Completed'`) | any in-flight phase — **including `stopping`** | **One atomic append** of the user and assistant entries; live turn cleared                                                                                              |
| `complete`, no `Finished`                                            | `awaitingFirst` / `streaming`                  | Cut-off entry (user + assistant-with-warning, keeping snapshot, timeline, and a Retry)                                                                                  |
| `complete`, no `Finished`                                            | `stopping`                                     | The stopped card — the server closed the body just as Stop was pressed; visually this is the stop the user asked for, not a cut-off warning                             |
| `error` `aborted`                                                    | `stopping`                                     | The stopped card: detached, "Stopped — not saved to this conversation", holding the flushed partial text; the optimistic user bubble goes with the live turn            |
| `error` `aborted`                                                    | `idle`                                         | Nothing — sign-out or a route-change reset already cleared the state, and patching would resurrect a turn (for sign-out, the previous user's)                           |
| `error` `aborted`                                                    | any other phase                                | The live turn is cleared, nothing else — clearing cannot resurrect anything, but a phase left in flight would wedge the composer on a Stop control with nothing to stop |
| `error`, any other arm                                               | `stopping`                                     | Prompt reseeded into the composer, **no** notice — the user wanted out; raising an error card for a turn they cancelled would be noise                                  |
| `error`, any other arm                                               | `awaitingFirst` / `streaming`                  | Pre-stream failure notice, with Retry where the arm is retryable                                                                                                        |
| Create `POST` failed (never reached the stream)                      | `creating`                                     | Failure notice without Retry + prompt reseeded (§3.2)                                                                                                                   |

Three decisions behind the table:

- **`aborted` is discriminated by phase, never by abort reason.** All three abort sources — Stop, sign-out, unsubscribe — surface as the same `aborted` arm ([streaming client §4.2](streaming-client.md#42-aborting--stop-sign-out-unsubscribe)). What distinguishes them is what the store did _first_: Stop flips the phase to `stopping` before aborting, while sign-out and route changes reset to `idle` before the rejection lands.
- **`Finished` racing a Stop wins.** The abort raced a clean close: the server transcribed the answer, so a card claiming "not saved" would be false — US-407's own honesty rationale.
- **The settle is one atomic patch.** The optimistic user bubble is a _field_ (`pendingUserText`), not a transcript entry — so `Finished` appends user and assistant entries in a single `patchState` (US-406's "exactly one append"), and Stop removes the bubble by nulling a field rather than popping an array.

A body that ends without `Finished` is US-406's deliberate **non-error** arm: a mid-stream fault and a network drop are indistinguishable on the wire ([contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame)), so the partial answer keeps its place in the transcript under a "This response was cut off" warning card, with a Retry that re-sends the turn's own prompt and selection — as sent, even if the pickers have moved since.

## 6. Stop

Stop (US-407) does different things depending on what there is to stop:

- **During `creating`** — the POST is cancelled and the prompt goes straight back into the composer. Nothing streamed, so there is no partial output to card.
- **During `awaitingFirst` or `streaming`** — the phase flips to `stopping` _before_ the abort, for the settle discrimination above, and because the patch is synchronous the Send control returns immediately rather than after the rejection's round trip.

The result is the **detached stopped card** (frame `1h`): dashed border and left inset, the design's statement that this text is _not_ part of the transcript — the server transcribed neither the answer nor the prompt, and the card vanishes on route change, reopen, and reload because the state behind it does. It offers:

- **Copy** — the partial text, with a two-second "Copied" confirmation (the focused button's label change is what announces it; a denied clipboard permission leaves the button as it was).
- **Retry** — restores the prompt into the composer _and_ the turn's model and MCP selection into the pickers, through the same `applyConversationSettings` seam US-410 seeds a reopened conversation with (§7.6) — and deliberately does **not** re-send. The selection restore routes through the twice-enforced MCP gating invariant ([streaming client §5.3](streaming-client.md#53-the-mcp-gating-invariant-enforced-twice)), so a selection that has gone stale cannot produce the server's 400.

There is no stop endpoint and none is called: aborting the fetch is what ends the turn server-side, and the server still records what the stopped turn spent.

## 7. Reopening a conversation (US-410, US-409)

Until this story, opening an existing conversation showed its title over an empty screen: nothing in the client had ever read `GET api/conversations/{id}/messages`. US-410 was written as "restore the model and MCP selection" and sized S; its third criterion is about a _refetched transcript_, which presupposed a fetch that did not exist, so history replay shipped inside it and it was re-sized to M (agreed with the product owner before implementation, and recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)).

Two independent restores happen on open, in two stores:

| What is restored            | From                                  | Owned by                                         |
| --------------------------- | ------------------------------------- | ------------------------------------------------ |
| The stored messages         | `GET api/conversations/{id}/messages` | `TurnStore` (§7.1–§7.5, §7.7)                    |
| The model and MCP selection | `GET api/conversations/{id}`          | `ConversationStore` → `TurnSettingsStore` (§7.6) |

US-409 rides along at the other end of the same round trip — after the _first_ turn completes, the conversation is re-read for the name the server generated from it (§7.8).

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

| Range           | Holds                    | Direction                             |
| --------------- | ------------------------ | ------------------------------------- |
| `-1, -2, -3, …` | Replayed history         | Descending, assigned in message order |
| `1, 2, 3, …`    | Turns taken this session | Ascending                             |

That split is what lets a refetch **rebuild the whole history block** without renumbering — or colliding with — entries taken since, keeping their `@for` identity stable across the swap. US-410's third criterion is the reason a rebuild is needed at all: after an aborted turn the server holds _fewer_ messages than the screen does, so a merge would leave the abandoned pair on screen forever, claiming the model saw them. A replace lets the transcript legitimately _shrink_.

The two callers disagree about one thing, `keepLive`:

- **A route-change read keeps live entries.** It was issued before any turn taken during it, so those entries are still to come and are not in the response.
- **The failure panel's Retry discards them.** It is issued _after_ everything on screen — the panel deliberately leaves the composer usable — so a turn taken while history was broken is already in the response about to be installed, and keeping it would render the exchange twice.

### 7.4 The message shape has two sharp edges

```jsonc
// GET api/conversations/{id}/messages
{
  "id": "…", "name": "…",
  "totalCount": 27, "hasMore": false,
  "messages": [
    {
      "id": "5b7e…",
      "dateCreated": "2026-08-15T20:27:57.0000000+00:00",
      "text": "…",
      "role": 3,
      "htmlContent": "<p>…</p>",
      "tokens": 412,
      "tokenAccuracy": "Estimated"
    }
  ]
}
```

- **`role` is a number, not a string.** The API registers no `JsonStringEnumConverter`, so `ChatRoles` serializes as its integer value: `1` System, `2` Assistant, `3` User, `4` Tool. [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts) mirrors that as `CHAT_ROLE`. Only `user` and `assistant` are rendered — the server already filters `System` out of the response, `Tool` is a model-to-model artefact whose proper surface is the activity timeline this response cannot reconstruct anyway, and an unknown role from a future server is skipped rather than guessed at.
- **Only `messages` may be read from the response.** It is `ConversationDto`-shaped on the wire but is rebuilt from the transcript's header document, which reports `projectId`, `modelId` and `isFavorite` as `null` / `null` / `false` whatever the truth ([shell §3.2](../ui/shell-and-navigation.md#32-get-apiconversationsid-versus-idmessages)). The client types it as its own `ChatConversationDto` with three fields rather than extending `ConversationDetailDto`, so that trap is unreachable through the type.

**A message is no longer bare.** Since the transcript moved to one Cosmos document per message ([transcript storage](transcript-storage.md)), each one carries `id`, `dateCreated`, `htmlContent`, `tokens` and `tokenAccuracy` beside `text` and `role`, and the envelope carries `totalCount` and `hasMore`. `text` and `role` are unchanged in name, type and meaning, so nothing here breaks a client that ignores the additions — but four of them are worth knowing before building on them:

- **`htmlContent` may be null**, in which case the client must render `text` itself. It is server-rendered by Markdig at persist time as an **export-fidelity artefact**, not a mirror of what this client draws: the two render through different pipelines (`ngx-markdown` over marked and Prism here) and differ in whitespace, syntax-highlighting markup and extension support. Nothing in the client reads it today, and swapping the transcript over to it would change how answers look.
- **`tokens` is the *context* cost, not what the turn was billed.** It is what the message will contribute to future prompts, estimated server-side from its own text, so an assistant message's `tokens` deliberately does not equal the turn's output tokens — tool-call arguments are generated, billed, and never transcribed. It is not the number the turn footer shows (§8.5), which is the provider's own report.
- **`tokenAccuracy` is a *string*** (`"Estimated"` or `"Exact"`) while `role` beside it is a number. That is not an oversight: the enum carries a property-level converter precisely because a global string-enum converter would have flipped `role` and broken `CHAT_ROLE`. Everything the server writes today is `"Estimated"`.
- **`dateCreated` is also the paging cursor.** `?take=` (clamped 1–100) and `?before={dateCreated}` return the newest page and walk backwards; `hasMore` says whether older messages remain. **Paging is opt-in — the no-parameter default still returns every message**, deliberately, so this client's replace-the-whole-history read (§7.3) keeps working unchanged.

**Two contracts, two shapes, one enum.** The stored Cosmos document serializes `role` as a camel-cased *string* (`"assistant"`), because that keeps an exported document readable and matches the SSE contract. The HTTP DTO serializes it as an integer, because this client depends on that. Reading the storage shape — through the JSON export, say — and the HTTP shape as though they were the same thing is the mistake this note exists to prevent.

**A message now carries per-turn `usage` over this route (US-1101), and this client does not read it yet.** An assistant message's `usage` (`{ inputTokens, outputTokens }`) is the turn's billed total, tools included — deliberately not the same figure as `tokens` above, and closer to what the message footer shows (§8.5) than to it, except that the footer's number comes from the live stream's own `Finished` event, not from a replayed history read. Nothing in `TurnStore` or `domain/api/conversation.ts` maps this field today, so a replayed turn's counts stay session-only exactly as §7.5 already describes — reading `usage` off history is additive work for whichever story chooses to close that gap, not something this release changed the shape of. The activity timeline is still absent from this route, and still permanent; §7.5 explains why.

### 7.5 Replayed history carries no activity timeline — permanently

The stream carries tool, MCP, agent and reasoning activity live and transcribes **only the answer text** ([contract §6.2](streaming-contract.md#62-only-the-answer-is-transcribed)). So a replayed turn is answer text and nothing else: no activity cards, no durations, no reasoning region (§8.6), no token footer (§8.5). A turn taken in the current session keeps all four until the screen is left, and loses them on reload by the same fact. Each is a structural absence rather than a check — `toHistoryEntries` writes an empty `reasoningText` and a null `reasoningSeconds` because there is nothing to write, and the replayed snapshot never folded a `Finished` to carry usage.

This is recorded as an interim-behaviour row in the [build order](../prd/enterprise-ui-rebuild-build-order.md) with **nothing planned against it** — closing it would need a server-side turn record, which is a new backend enabler, not a client story. Anything a user must keep from a stopped or cut-off turn still goes through the stopped card's Copy (§6); nothing else preserves it.

Any change of conversation — another sidebar row, "New conversation", a deep link — still aborts a live turn and clears the transcript, the stopped card, and every notice before the replay starts. Sign-out does the same through `withResetOnSignOut` plus an explicit abort, and `loadHistory` composes `takeUntil(signedOut$)` for the repo's standing reason: clearing state is not cancelling work.

### 7.6 Restoring the model and MCP selection

`ConversationStore` seeds `TurnSettingsStore.applyConversationSettings({ modelId, mcpServerIds })` from the `GET api/conversations/{id}` response — the only shape carrying `mcpServerIds`. The seed is deliberately **unvalidated**: `selectedModel` falls back to the catalog default for an id that is gone, and `effectiveMcpServerIds` re-intersects with the permitted catalog, so a stale selection structurally cannot reach the wire ([streaming client §5.3](streaming-client.md#53-the-mcp-gating-invariant-enforced-twice)). The same re-intersection also drops a server whose auth type is `UserApiKey` if the caller no longer has a usable key for it (`needsUserApiKey`, [`domain/api/mcp.ts`](../../enterprise-gpt-ui/src/app/domain/api/mcp.ts)) — a selection made while a key was stored can outlive that key, since removing or losing one does not touch the conversation's saved selection, and the picker itself never lets such a server be toggled on in the first place (§8.7).

Two things in it are easy to get wrong.

**An empty payload is not a selection of "nothing".** The server writes `modelId` and the MCP set from the newest `ConversationUsage` row, so a conversation whose first turn has **not completed** reports `modelId: null` and `mcpServerIds: []` — which is exactly the state US-401's `replaceUrl` re-opens this store in, while that first turn is still streaming. Seeding it would clear the model and tool servers the user picked for the turn running right now: US-402 and US-403 undone by a round trip. The seed is therefore skipped when both are empty.

**A deactivated model has to be reported, not absorbed.** `restoredModelId` is remembered separately from the selection so the fallback to the default can be explained rather than happening silently; `restoredModelUnavailable` then gates on the catalog having actually _resolved_, because every deep link races the catalog load and an empty `models()` would otherwise flash "your model was deactivated" at every conversation and then withdraw it. The composer renders the result as an amber note above the control row — a state no design frame draws, so it reuses frames `2d`/`2j`'s treatment (§9.1). Picking a model clears `restoredModelId`: the user has answered the warning. Opening the empty screen clears it too, through `clearRestoredModel()` — there is nothing resumed there to explain — while the selection itself persists across conversations for the session, as US-402 requires.

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
- **`wasFirstTurn` is read _before_ the settle** appends the entry that would make this turn look like it had a predecessor. It derives from "the transcript holds no `assistant` entry", which is why a cut-off attempt — which leaves a `cutOff` entry, never an `assistant` one — still counts the next attempt as first, while §7's replayed history correctly marks an existing conversation as not first.
- **An unread history is not an empty one.** The flag is false when `historyError` is set, or the client would refetch a name the server settled long ago.

`ConversationStore` handles the event with a **silent** `refreshDetail`, not `open` or `retry`: those go through `setPending()` and would blank the header mid-conversation, flashing the sidebar's stale name back for a round trip. Its failure is swallowed — everything on screen is still true, and a name arriving one refresh late does not deserve an error panel — and the swallow lives inside `tapResponse` rather than a `catchError` ahead of it, because a throw escaping there would kill the `rxMethod` and every later first turn would silently stop refreshing. The sidebar needed no new code: `refreshDetail` calls the same `ConversationListStore.refreshRow` seam the detail load already used, unguarded by the open conversation id, so the row earns its generated name even if the user has moved on mid-round-trip.

## 8. Rendering the turn

[`Transcript`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts) is the conversation column: the history skeleton or its failure panel first (§7.1, §7.7), then the settled entries, then whichever live state applies — the thinking ridgeline, the streaming turn, a failure notice, or the stopped card. Its accessibility posture is deliberate, and since US-1402 four ARIA live regions are in play around it, only two of them its own:

- `aria-busy` rides the container for the length of a turn **and while the stored messages load**, so assistive tech treats the region as settling rather than announcing every re-render. Since US-1402 it releases on the folded `Finished` — `historyPending() || (inFlight() && !answerComplete())`, where `answerComplete` reads `snapshot.phase() === 'Completed'` — which is one step earlier than `!inFlight()` and is what leaves the live turn mounted at the moment its own deferred live region is released.
- The **streaming answer** is a polite live region carrying its own `aria-busy`, and the settled turn that replaces it carries no `aria-live` at all, so an answer is announced exactly once. The activity cards and the reasoning region inside it are `aria-live="off"`. All of that is US-1402's and is written up in [Answer Rendering §10](../ui/answer-rendering.md#10-following-a-streaming-answer-with-a-screen-reader-us-1402), including the `aria-busy` release it has not yet been possible to verify against a real screen reader.
- A **persistent** visually-hidden `role="status"` region announces abnormal endings only — stopped, failed, cut off. It exists from the first render because a live region created together with its content is not reliably announced, and it empties during every turn so a retry that cuts off _again_ transitions the content and is announced a second time.
- A **second** `role="status"` region carries US-603's copy confirmation, and is separate from the first on purpose: that one is emptied for the length of a turn, while a code block can be copied mid-turn. `Transcript` also carries the single delegated click listener behind every code block's Copy control; both belong to [Answer Rendering §3.5](../ui/answer-rendering.md#35-the-code-block-chrome-and-its-copy-control-us-603) and are not restated here.
- The **running commentary** a reader actually hears mid-turn — "Get forecast is running", "Writing the answer", a failure count — is a third `role="status"` region, and it is rendered by [`Chat`](../../enterprise-gpt-ui/src/app/features/chat/chat.html) **outside** this container on purpose: anything inside it is deferred along with the answer. It speaks `TurnStore.turnStatus`, which is `''` at rest, `'Answer ready'` from the folded `Finished` until the settle, and otherwise the pure `turnAnnouncement` fold in `domain/stream/`.
- **US-607's message-level Copy adds no region of its own**, and that asymmetry is deliberate rather than an oversight: it is a real template-bound button that exists only on a settled turn, so changing its own accessible name announces the confirmation on the element the reader just activated ([Answer Rendering §9.1](../ui/answer-rendering.md#91-why-this-one-needs-no-live-region)).

An assistant turn renders top to bottom as: the reasoning region (§8.6), then the text blocks and activity cards in arrival order, then the message footer (§8.5) — which since US-607 carries a Copy control on the prompt bubble as well as under the answer.

[`AssistantTurn`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts) renders the avatar beside all of that, and the middle of it is the render-time join of §4. The blinking caret belongs to the last node, and only when it is a text block. Text renders as **sanitized markdown** as of US-601, and the node still growing renders as two `<markdown>` instances — a stable head and a volatile tail (US-602). Neither the pipeline nor the split is restated here: see [Answer Rendering](../ui/answer-rendering.md). Two facts from it change how this screen behaves, though — the renderer writes its output a microtask _after_ change detection returns, which is why US-606's scroll pinning is driven by a `ResizeObserver` rather than by these signals; and the caret is now a CSS pseudo-element on the rendered block, because `innerHTML` leaves no template position inside it for a span.

Reasoning takes **no timeline node**: it is not a block of the answer and never interleaves with one, so it opens the turn rather than being placed by arrival order.

[`ActivityCard`](../../enterprise-gpt-ui/src/app/features/chat/transcript/activity-card.ts) is frame `1f`: the activity's `displayName` put through [`activityLabel`](../../enterprise-gpt-ui/src/app/domain/stream/activity-label.ts) as the label, the kind as a **separate** badge — the contract keeps them apart so no card ever reads "Calling Jira Cloud MCP" — the `source` subtitle verbatim (the "Atlassian · host" composition is the server's, arriving as one string), sub-status bullets, and a running ring / completed check / failed triangle with durations in mono (`3.1s`, absent until completion). It now also nests the work it invoked (§8.4) and reveals what it cost behind a chevron (§8.5). [`KindBadge`](../../enterprise-gpt-ui/src/app/shared/badge/kind-badge/kind-badge.ts) is pinned to exactly four labels, one per `ToolKind` arm — Function, MCP tool, Agent, and **Reasoning** for `Unknown`, the kind reasoning phases stream under (previously mislabelled "Tool"; US-501 completed the set); its only US-502 change is that it reads its background from a `--kind-badge-bg` custom property, so a nested card can invert it against its own surface without a new input.

**`activityLabel` is a carve-out for one kind, not a formatter for all of them.** Since `Andes.Extensions.AI.UI` 0.7.0 an `McpTool` activity's `displayName` is the raw tool name the model called — `Weather_get_forecast` for a tool this API leased, because the server prefix travels with the name — and the server itself is already on the card as `source` ([streaming contract §4.2](../conversations/streaming-contract.md#42-fields)). The function strips that prefix using the card's own `source`, turns the remaining underscores into spaces and upper-cases the first character, so a card that used to read "Weather" over "Weather" now reads **Get forecast** over **Weather**. `Function` and `Agent` names are returned untouched: a function name is a code identifier the design deliberately sets in mono, and reflowing it into prose only to render that prose in a monospace face is the worst of both, while an agent's name is already a display string. The live region derives its own text from the same function, so the card and the announcement cannot disagree — but they are two computations rather than one shared value, which is why both are pinned by specs.

[`TurnNoticeCard`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-notice-card.ts) carries every turn-edge notice, and now has four designed arms plus a fallback:

| Arm                        | Frame `1h` variant                                                    | Story           |
| -------------------------- | --------------------------------------------------------------------- | --------------- |
| Cut off                    | Warn panel, triangle, Retry                                           | US-406          |
| Tool server unavailable    | Plain `--surface` card, plug icon, names the server, **Retry**        | US-403 / US-406, US-414 / US-415 |
| Conversation busy          | Warn panel, **one row** — hourglass, one sentence, Retry pushed right | US-408 (§8.1)   |
| Anything else              | Generic warning with the `userMessage` and the `traceId` line         | —               |

The fallback is deliberate rather than lazy: an arm with no designed frame gets the honest generic treatment instead of invented copy. The card gained a `layout` axis (`stack` | `row`) for the busy arm, which is the only one-line variant the design draws.

### 8.1 The busy warning (US-408)

Most of this story was already structural. `settlePreStream` lands the typed 409 in `turnError` with its retry snapshot, `canRetry` says yes for `conversation-busy`, and `isTransientRetriable` says no to every problem-typed arm — so "no automatic retry at any interval" holds because nothing in the client can schedule one, not because a scheduler was omitted. What shipped is the frame: `--warn-bg` panel, `--warn-border`, hourglass, the sentence "Another response is still running in this conversation.", and Retry on the far right. **No spinner** — nothing is being awaited — and **no trace id**, because a second tab holding the conversation lock is not a defect to correlate.

The copy is hard-coded beside the tool-server arm's rather than taken from `userMessage`, which has to say "This conversation…" for a toast that could fire from anywhere; a card sitting directly under the prompt that caused it can say "another response" and be clearer for it.

**One accessibility defect was found in review and fixed here**, and it is the reason `TurnNoticeCard` exposes a `focusRetry()` method. Retrying destroys the button that was pressed — the send clears the notice — so focus falls to `<body>` for the length of the attempt; the composer's settle fixup (§9) then claimed it for an empty prompt box, and a keyboard user re-traversed the page on **every** attempt of exactly the loop this story is about. Two changes close it: `Transcript` hands focus to the replacement Retry when focus has fallen through, and the composer's fixup stands down while a notice is on screen, because the prompt a retry re-sends lives in the notice rather than in the box. Both are conditioned on "focus is nowhere" — `null`, `<body>`, or a detached element — never on "the user retried", so a notice raised while the user is somewhere real never steals focus from them.

### 8.2 The MCP consent card is gone (US-414 / US-415)

US-412's consent card, and the 403 `/problems/mcp-authorization-required` it rendered, are deleted. Tool servers are provisioned for users by an administrator who consents tenant-wide, so there was never a per-user consent state for this card to be honest about — it named a server and told the reader to contact an administrator for a requirement that did not exist. **US-411**, the enabler that would have put a `scope` on that problem so a future "Authorize _{serverName}_" action could request it, is withdrawn for the same reason: there is nothing here for a user to consent to.

`MicrosoftIdentityWebChallengeUserException` and every `MsalException` — `MsalUiRequiredException` included, by derivation — now raise `McpServerUnavailableException` instead, so a token acquisition that cannot complete without user interaction reaches the client exactly like any other unreachable server: 502 `/problems/mcp-server-unavailable`, rendered by the **tool-server-unavailable card** in the table above. That card is otherwise unchanged, and is now the only card a failed tool acquisition can produce.

**One behaviour change worth stating rather than discovering: the card grew a Retry.** `mcp-server-unavailable` is `true` in `RETRYABLE_KINDS` ([`error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts)), where `mcp-authorization-required` was `false` — the consent card offered none. That is accepted deliberately: 502 also covers genuinely transient failures a retry can fix, and a second attempt against a mis-registered server just fails the same way instead of misleading the reader about who can fix it.

Roughly a dozen specs had used the consent fixture to pin *general* invariants — a 403 never triggers a token refresh, a typed 403 is never retried, a problem body parses into its arm — and each was retargeted to an ordinary 403 or to this 502 rather than deleted, since the invariant, not the fixture, is what mattered. Review caught the one spec that lost real coverage in that move — the transcript's `[role="status"]` live region on the **error** path, which is what a screen reader hears and which no other test asserted — and it is restored.

### 8.3 Who decides whether Retry appears

Three layers, each answering a different question, and they must not be collapsed:

| Question                                                       | Answered by                                                                                            |
| -------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| Does a re-sendable turn exist at all?                          | `Transcript`, from the store's retry snapshot — passed to the card as `retryable`                      |
| Could a person pressing Retry plausibly get past this failure? | `canRetry` ([shell §8](../ui/shell-and-navigation.md#8-canretry--whether-to-offer-the-control-at-all)) |
| May the interceptor replay this automatically?                 | `isTransientRetriable` — never for a 409, never for a problem-typed 503                                |

The card itself decides none of them — which before US-415 was why the consent arm showed no action even though the store kept a retry snapshot, and is now why the tool-server-unavailable card does show one.

### 8.4 Nested activities, and one that failed (US-502, US-505)

Both stories are **render-side only**. The vendored fold already hangs a child off the parent named by its `parentScopeId`, and already isolates a failure to the scope the `ActivityFailed` names — so `ActivityCard` walks `children` and recurses into itself (a standalone component is in scope of its own template and needs no entry in its own `imports`), and a failed child structurally cannot mark its parent. What US-502 changed is that this is now _observable_: the child is drawn inside its parent rather than beside it.

**The filter is root membership, not `parentScopeId`.** `AssistantTurn` opens a top-level card only for a timeline node whose scope is a **root** of `snapshot.activities`. The obvious test — `node.parentScopeId !== undefined` — is wrong in both directions:

- The fold **promotes** a child whose parent scope it has never seen to a root. That card carries a `parentScopeId` and has to stay visible; dropping it loses a whole tool call, where keeping it is at worst an unattached card.
- It does the same for a child whose parent arrives _late_, for the same reason.

Roots are **consumed from a per-`scopeId` FIFO** rather than looked up. The fold defaults a missing scope id to `''` rather than dropping the event, so two scope-less roots share a key and a plain lookup would render one of them twice. A single pointer walking `activities` in order would also fix that, but it rests on an ordering invariant that lives in the vendored contract: one mismatch stalls the pointer and drops every card after it, where the same mismatch costs a per-id queue exactly one card.

Depth styling is **per-level CSS custom properties** (`--activity-rail`, `--activity-gap`, `--activity-glyph`, `--activity-name`, `--kind-badge-bg`), not descendant selectors. A nested card is a separate component instance, so its own declarations override anything it inherits, and every rule reads one name whatever depth it renders at. Three levels alternate surface, radius and glyph size; a fourth reuses the third's, because the design draws three and an agent tree nests arbitrarily. **The rail is 12 px at every level**, per US-502's literal criterion — the board draws the second at 10 px, and that 2 px is deliberately one rule rather than two. Below 768 px only the **outermost** rail insets (frame `1d`'s "one indent level"): the deeper ones keep their border, which is what says "inside", but stop compounding the ~33 px each costs, which on a 390 px viewport leaves a third-level card no room for its label.

US-505 shipped one thing beyond what was already structural: the card's `visually-hidden` state line now carries the duration — **"Failed after 0.6s"**, not bare "Failed". Colour and glyph were the only channels for it, and "Failed" alone says nothing about whether the call timed out or refused immediately.

> **Frame `1f`'s `--warn` failure-reason line is deliberately not built.** `ActivityFailed` carries `scopeId` and `durationSeconds` and nothing else, so there is no failure text on the wire and writing one would be the fabricated status line US-406 refuses. Recorded as an interim behaviour in the [build order](../prd/enterprise-ui-rebuild-build-order.md); closing it needs a backend enabler putting failure text on the event.

### 8.5 What a turn cost (US-504)

Two surfaces, one formatter — [`turn-usage.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-usage.ts), pinned to **`en-US`** rather than the ambient locale. The app ships one language, the design fixes the grouping separator, and a formatter that followed the host would render `1.842` on a German machine and make every spec depend on where it ran.

| Surface                                | Renders                                            | Where                             |
| -------------------------------------- | -------------------------------------------------- | --------------------------------- |
| The message footer (frames `1f`, `1g`) | `1,842 in · 512 out · 2,354 total`, JetBrains Mono | Below the answer, `AssistantTurn` |
| The per-activity strip (frame `1f`)    | `duration  3.1s`, behind the card's chevron        | Inside `ActivityCard`             |

**Absence is the rule that matters.** A count the provider did not report is **omitted**; a reported `0` is a measurement and renders. Three "claims nothing" cases fall out structurally rather than from a check, and all three are pinned by specs: a stopped turn keeps only its text, a cut-off turn never folded `Finished`, and a replayed turn never had a stream (§7.5).

**The footer is now revealed on hover and focus** (US-607), which is what frame `1g` annotates and what US-504 could not ship: a hover-only footer is unreachable by keyboard and touch until it holds something focusable, and that is the Copy control — also what the criterion meant by "beside". Two consequences for this screen, both owned by [Answer Rendering §9](../ui/answer-rendering.md#9-copying-a-prompt-or-a-response-us-607) rather than restated here: **US-504's gate inverted**, so the footer is unconditional for a settled turn and the counts are the conditional child, since Copy is offerable on a stopped, cut-off or replayed turn that has no counts at all; and the **optimistic user bubble renders an empty footer**, so a prompt row occupies the same box before and after its turn settles rather than growing ~28 px under the reader.

One honest gap remains, recorded as an interim behaviour in the build order:

- **The activity strip shows `duration` only.** No server populates `AssistantActivity.usage`: the contract carries `usage` on `Finished` alone and the vendored fold never writes the field. Rendering the duration there rather than gating the whole strip on tokens is what keeps the chevron a live affordance instead of dead markup, and it is what the frame draws — the expanded card shows the duration in the header _and_ in the strip. The token columns arrive the moment something attributes them; the field is already in the type.

A **running** activity has no chevron at all, because nothing has settled to show. Children are _not_ behind the chevron — the frame draws them open, and hiding an agent's work behind a control is the opposite of what the timeline is for; the chevron reveals the cost strip alone.

### 8.6 The reasoning region (US-503)

[`ReasoningRegion`](../../enterprise-gpt-ui/src/app/features/chat/transcript/reasoning-region.ts) is frame `1f`'s pill: collapsed, a `--think-bg` pill carrying a right chevron, the label "Reasoning" and the measured duration in JetBrains Mono; expanded, the same tint as a card, a down chevron, and the summary in `--muted`.

Four decisions are worth knowing before changing it:

- **The seeded sentence is never rendered.** A turn whose only reasoning was the server's seed shows **no region at all** (§4.2). The store hands the component `modelReasoningText`, never `snapshot.reasoningText` — only the store knows how long the seed was.
- **The ridgeline hands the gap over.** `showThinking` / `showLiveTurn` split on "is the model reasoning" (§2), so the region streams inside the live turn from the model's first delta rather than appearing whole at the first token — one component instance for the length of the turn.
- **The text renders as plain text, not markdown.** It is a summary written for a reader, and a third `<markdown>` instance per turn would spend the streaming budget US-602 exists to protect. `pre-wrap` keeps the model's paragraph breaks, which are the only structure a reasoning summary has.
- **Expansion is component-local state**, which is exactly "persists for the duration of the turn": the live turn's instance is stable while it streams, and the settled turn is a different instance whose default is collapsed again.

### 8.7 A selected server needing the caller's own API key

`mcp-credential-required` and `mcp-credential-rejected` (both `428 Precondition Required`, see [streaming contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame)) render through the same generic notice-card path as every other pre-stream failure in §8.3's table, with `canRetry` returning `false` for both: the way past either is supplying a key, and a Retry button that repeats the same request would just repeat the same 428. `userMessage` words the two differently — "Connect your account to use the tool server…" for the first, "…rejected your API key. Enter a new one." for the second — but neither points the reader anywhere from the card itself; fixing it means reopening the composer's Tools menu and using the masked-key row §7.6 already dropped the server behind ([Administration §11](../ui/administration.md#11-the-mcp-server-registry-us-1208-us-1210) documents the registration side, and the [GitHub MCP Server runbook](../mcp/github-server.md) documents this end to end for the motivating case).

Both are excluded from `shouldNotify` too — `false` in `NOTIFIABLE_KINDS` — so a mid-turn instance of either raises no toast alongside the inline card: the card *is* the message, and a toast beside it would say the same sentence twice. This mid-turn path is the **rare** case in practice, since the same catalog fact that produces it (`needsUserApiKey`) is what keeps the picker from letting such a server be toggled on in the first place (§7.6) — reaching it means a key that was valid when the turn was sent stopped being valid before the server actually ran the request.

## 9. The composer and the empty state

The Send and Stop controls are **one persistent button** that morphs with `inFlight` — the element survives the swap, so keyboard focus does too. One settle case still strands focus: a turn settling into an empty composer re-disables the morphed Send while it is focused, and engines disagree about what happens next (some drop focus to `<body>`, others leave it on the disabled control) — both arms are caught, and focus is handed to the textarea. Only a fall-through is corrected; a user focused elsewhere is left alone, and since US-408 the fixup also stands down when the turn settled into a notice, whose Retry is the thing worth focusing (§8.1).

Composer text seeding flows **one way, through the store**: `seedComposer` writes a one-shot seed (a fresh object per seed, so seeding the same text twice still propagates), the composer applies it, focuses the textarea, and acknowledges through `consumeComposerSeed`. Three producers use it — the suggested prompt chips, Stop-during-create restoring the prompt, and the Retry restores — and `protectedState` stays intact throughout. Dictated phrases use the same one-way handoff but a different signal (§9.2), because they _append_ rather than replace.

### 9.1 The amber note is one message at a time

The line above the control row shows at most one message, ordered by what it costs the user not to know:

1. **The model catalog failed** (frame `2j`) — with no model there will be no turn, which makes everything below it moot.
2. **The restored model is unavailable** (US-410, §7.6) — it explains a selection the user did not make, and changes what the next turn costs.
3. **Tools were cleared by the model choice** (frame `2d`) — it explains a selection they did make.
4. **The microphone was blocked** (US-413) — it explains a control that failed.

The microphone ranks last on purpose: one denied permission must not hide the fact that the conversation is now answering on a different model.

### 9.2 Dictation (US-413)

Speaking a prompt runs on the browser's own Web Speech API. **Enterprise GPT never receives the audio**: no microphone stream reaches this application's code, nothing is uploaded to the API, and there is no server-side transcription service anywhere in the product — the PRD's "voice input" was scoped to browser-native recognition for exactly that reason.

That is a statement about _this_ application, not about the browser. Several engines — Chrome's included — perform recognition remotely, sending the captured audio to the browser vendor's own speech service, so a regulated tenant should evaluate the browser's behaviour rather than assuming the audio stayed on the machine. Nothing in the client can change that; what the client controls is that it neither handles nor stores the audio itself.

**Absence, not disablement, where it is unsupported.** [`SPEECH_RECOGNITION`](../../enterprise-gpt-ui/src/app/core/speech/speech-recognition.ts) resolves `SpeechRecognition ?? webkitSpeechRecognition` and returns **null** where neither exists — Firefox today, and any non-secure origin. The composer asks the token whether to draw the control at all, so "absent rather than present-and-disabled" is a rendering consequence rather than a check someone has to remember. It is a DI token for the same reason as `STREAM_FETCH`: specs substitute a fake through a provider override instead of assigning to `window`, which is necessary here because the real interface exists in no test environment.

**The types are declared locally, on purpose.** TypeScript 5.9's `lib.dom.d.ts` ships `SpeechRecognitionResult`, `SpeechRecognitionResultList` and `SpeechRecognitionAlternative` but **not** `SpeechRecognition` itself, its event types, or the prefixed constructor. The file declares the four members US-413 uses and nothing more, rather than adding a dependency for four lines of types.

[`DictationStore`](../../enterprise-gpt-ui/src/app/features/chat/composer/dictation-store.ts) is **component-provided** by `Composer`, not root: a recording session belongs to the prompt box that started it, and its `onDestroy` aborts — nothing else releases the microphone, and the engine holds the device until told otherwise. Five behaviours are worth knowing:

- **`interimResults` stays off**, against the spec's own default. Interim guesses rewrite themselves word by word, and a textarea the user may also be typing in is the wrong place to watch that happen. Phrases land once they are final, which is also the only form that survives being edited afterwards.
- **Phrases append.** Speaking after typing, or after an earlier phrase, extends the prompt. The store holds one finalized phrase at a time in `spoken` (a fresh object per phrase, so the same words twice still propagate) and the composer consumes it, exactly as it consumes `composerSeed`.
- **Stopping uses `stop()`, never `abort()`**, so the engine emits the last phrase it heard. `abort()` is reserved for teardown, where the handlers are detached first so a destroyed store is not patched.
- **A denied microphone is explained, and the composer stays usable.** `not-allowed` and `service-not-allowed` set `permissionDenied`, which raises the note in §9.1; the flag is held until the next attempt rather than cleared immediately, because a browser told to block will not prompt again and the message has to outlive the click that earned it. Every other error — no speech, no network, an engine failure — ends the session silently: a message about `audio-capture` explains nothing a user can act on.
- **The control is one morphing button**, like Send/Stop. Frame `2i` puts the recording pill in the microphone's place — `rgba(33,168,216,.14)`, `ringpulse 1.8s ease-out infinite` (one of the design's four existing keyframes; no fifth was added), a filled mic glyph and the elapsed `m:ss` in JetBrains Mono — and morphing rather than swapping is what keeps focus on the control the user just pressed. The changing `aria-label` ("Dictate" → "Stop dictating") on that still-focused button is also what announces that recording started.

While a turn streams, **starting** is disabled and **stopping** is not: a user must always be able to release the microphone. The idle microphone carries `composer__aux`, so US-407's in-flight dimming applies to it and that rule finally matches a control; the recording pill deliberately does not, because a dimmed control that still works reads as broken.

The empty state (frame `1a`) closes US-303's deferral: the four suggested prompt chips — _Summarize a document_, _Draft a status update_, _Find open Jira blockers_, _Explain a code change_ — render on the landing screen only, and a chip's entire behaviour is seeding the composer's text. No focus is moved on arrival, because this is the application's landing screen rather than a state transition, and taking focus would jump a keyboard user past the shell's skip link on every cold start.

## 10. Testing

`TurnStore`'s specs drive the send pipeline with a substituted `ConversationStreamClient` and settle every row of §5's table, including the races — `Finished` against Stop, a route change against a live create, a batch against a reset. The timeline reducer is framework-free and tests in Node beside the codecs ([`turn-timeline.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.spec.ts)); the transcript and composer specs cover the join, the caret, the announcements, and the focus fixups.

EP-4's closing five added four seams to that:

- **Replay** is framework-free too — [`replayed-turn.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/replayed-turn.spec.ts) runs in Node, and `TurnStore`'s specs cover the id ranges, both `keepLive` arms, the shrinking transcript, and the read cancelled by a route change.
- **`turnEvents.completed`** is asserted on both sides: dispatched only on `Finished` and with the right `wasFirstTurn`, and handled by `ConversationStore` without going pending.
- **The `RETRYABLE_KINDS` table** is pinned in both directions, which is what keeps the permission-required arm actionless and the busy and tool-server-unavailable cards actionable.
- **Dictation** substitutes `SPEECH_RECOGNITION` by provider override with the fake in [`@testing/speech`](../../enterprise-gpt-ui/src/testing/speech.ts) — the real interface exists in no test environment, so there is nothing else to drive it with.

EP-5's four added two more:

- **The reasoning window** is framework-free, so [`reasoning-timing.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/reasoning-timing.spec.ts) runs in Node and drives the clock as an argument: the seed excluded from both the text and the measurement, an activity closing the window as well as a `TextDelta`, reasoning resuming mid-answer neither restarting nor extending it, `Finished` closing an open window, and a seed-only turn claiming nothing.
- **The render side** is pinned in `transcript.spec.ts` — a child drawn inside its parent, a parent-less child kept at the top level, the three levels stepping their surface and rail, a failed child leaving its parent alone, the footer appearing only for a turn that folded `Finished`, the chevron absent while an activity runs, and the region present while reasoning streams but absent for a seed-only turn and for replayed history.

The epic closed with the suite at **1010 specs across 83 files**, all green, and `npm run lint`, `npm run format` and `npm run build` clean. The initial bundle moves ~1 kB to **661.36 kB raw / 161.19 kB transfer** (665 kB warn / 720 kB fail), with `styles` at 63.19 kB — and that kilobyte is global CSS for US-603's code-block chrome, not this screen: every component and reducer these four stories added rides the lazy `chat` chunk, which grew 187.59 → **198.51 kB**.

The MCP label carve-out landed after that and added one more framework-free seam. [`activity-label.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/activity-label.spec.ts) runs in Node and pins the whole decision surface: the prefix stripped and humanized, a server name sanitized the way the API sanitizes it, a prefix differing only in case **not** stripped, a name that is exactly the server name left alone, a remainder that would render blank rejected in favour of the raw name, an acronym surviving because only the first character is upper-cased, and every non-`McpTool` kind returned untouched. The card and the live region are asserted separately, in `transcript.spec.ts` and `turn-announcement.spec.ts`, because they are two computations over one function rather than one shared value — including that the chevron's accessible name quotes the label a user can *see* rather than the raw `displayName`.

## 11. Key files

| Concern                                                              | File                                                                                                                                                                                                                                                                                                           |
| -------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Turn store — phases, transcript, settles, history replay             | [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts)                                                                                                                                                                                                                   |
| Arrival-order index                                                  | [`domain/stream/turn-timeline.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.ts)                                                                                                                                                                                                             |
| Reasoning window — the seed's length and the elapsed clock           | [`domain/stream/reasoning-timing.ts`](../../enterprise-gpt-ui/src/app/domain/stream/reasoning-timing.ts)                                                                                                                                                                                                       |
| What a screen reader is told while a turn runs (US-1402)             | [`domain/stream/turn-announcement.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-announcement.ts), spoken by [`features/chat/chat.html`](../../enterprise-gpt-ui/src/app/features/chat/chat.html)                                                                                                     |
| Replaying a stored answer, and the shared synthetic event            | [`domain/stream/replayed-turn.ts`](../../enterprise-gpt-ui/src/app/domain/stream/replayed-turn.ts), [`domain/stream/stream-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/stream-codec.ts)                                                                                                           |
| Stored-message shape and roles                                       | [`domain/api/conversation.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation.ts)                                                                                                                                                                                                                     |
| Turn completion event                                                | [`core/events/turn-events.ts`](../../enterprise-gpt-ui/src/app/core/events/turn-events.ts)                                                                                                                                                                                                                     |
| Conversation detail, settings seed, silent name refresh              | [`features/chat/conversation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/conversation-store.ts)                                                                                                                                                                                                   |
| Transcript container, live announcements                             | [`features/chat/transcript/transcript.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts)                                                                                                                                                                                             |
| Assistant turn — the render-time join                                | [`features/chat/transcript/assistant-turn.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts)                                                                                                                                                                                     |
| Activity card — nesting, depth styling, the cost chevron; kind badge | [`features/chat/transcript/activity-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/activity-card.ts), [`shared/badge/kind-badge/kind-badge.ts`](../../enterprise-gpt-ui/src/app/shared/badge/kind-badge/kind-badge.ts)                                                                     |
| Reasoning region — frame `1f`'s pill and its expanded card           | [`features/chat/transcript/reasoning-region.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/reasoning-region.ts)                                                                                                                                                                                 |
| Token counts and durations — the one formatter for both surfaces     | [`features/chat/transcript/turn-usage.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-usage.ts)                                                                                                                                                                                             |
| Turn-edge notices                                                    | [`features/chat/transcript/turn-notice-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-notice-card.ts)                                                                                                                                                                                 |
| Stopped card                                                         | [`features/chat/transcript/stopped-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/stopped-card.ts)                                                                                                                                                                                         |
| Composer — morphing button, seeding, the amber note                  | [`features/chat/composer/composer.ts`](../../enterprise-gpt-ui/src/app/features/chat/composer/composer.ts)                                                                                                                                                                                                     |
| Dictation — token and session store                                  | [`core/speech/speech-recognition.ts`](../../enterprise-gpt-ui/src/app/core/speech/speech-recognition.ts), [`features/chat/composer/dictation-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/composer/dictation-store.ts)                                                                             |
| Turn settings — restored model, gating invariant                     | [`core/chat/turn-settings-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/turn-settings-store.ts)                                                                                                                                                                                                         |
| Empty state and chips                                                | [`features/chat/chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts)                                                                                                                                                                                                       |
| Chat screen — providers, route binding                               | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts)                                                                                                                                                                                                                               |
| Markdown rendering, the streaming split, scroll pinning              | [`features/chat/markdown/`](../../enterprise-gpt-ui/src/app/features/chat/markdown/), [`domain/markdown/streaming-split.ts`](../../enterprise-gpt-ui/src/app/domain/markdown/streaming-split.ts), [`features/chat/transcript-pinning.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts) |
| Related reference                                                    | [Conversation Streaming Contract](streaming-contract.md), [Conversation Streaming Client](streaming-client.md), [Answer Rendering](../ui/answer-rendering.md), [Shell & Navigation](../ui/shell-and-navigation.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md)                               |
