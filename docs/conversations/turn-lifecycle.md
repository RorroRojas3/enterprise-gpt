# Conversation Turn Lifecycle

The layer above the [Conversation Streaming Client](streaming-client.md): what happens between pressing Send and a settled transcript entry. Shipped by four stories — US-401 (send a first prompt; the conversation is created *around* it), US-406 (streamed answer rendering), US-407 (Stop, keeping the partial output), US-501 (the chronological activity timeline) — which also closed two recorded deferrals: US-303's suggested prompt chips and US-403's tool-server-unavailable card. Audience: whoever builds the stories on top — the busy and consent treatments (US-408, US-412), turn events (US-409), history replay (US-410), nested activities (US-502), markdown (US-601) — or debugs a turn that ended strangely. The wire format lives in the [contract](streaming-contract.md); the codec, transport, and turn settings live in the [streaming client](streaming-client.md) — neither is restated here.

## 1. Overview

One route-scoped store owns everything: [`TurnStore`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) holds the session transcript, the live turn, and the send flow, provided by `Chat` beside `ConversationStore` — so its lifetime is the chat screen's, and leaving `/chat` unsubscribes the stream, which aborts the fetch (there is no stop endpoint; [streaming client §4.2](streaming-client.md#42-aborting--stop-sign-out-unsubscribe)).

| Concern | What it does | Where |
|---|---|---|
| Turn state | Phases, transcript entries, live snapshot + timeline, settle logic | `features/chat/turn-store.ts` |
| Arrival-order index | Which text blocks and activity cards arrived in what order | `domain/stream/turn-timeline.ts` — framework-free, beside the vendored fold |
| Transcript rendering | Settled entries, the live turn, the notices, the stopped card | `features/chat/transcript/` |
| Send / Stop control | The morphing button, composer seeding, focus recovery | `features/chat/composer/` |
| Landing screen | The wordmark, prompt heading, and four suggested prompt chips | `features/chat/chat-empty-state.ts` |

Two structures describe one live turn, and neither duplicates the other (§4). The transcript is **session state**: it exists only while the chat screen stays on the same conversation, and history replay is deliberately not here — that is US-410 (§7).

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

`awaitingFirst` advances to `streaming` only when a batch carries **renderable** content — an `ActivityStarted` or a `TextDelta`. A batch of `Status` or `ReasoningDelta` events deliberately does not clear the thinking indicator: this app emits no request-level status line and fabricates none (US-406).

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
- **Retry** — restores the prompt into the composer *and* the turn's model and MCP selection into the pickers, through the same `applyConversationSettings` seam US-410 will use — and deliberately does **not** re-send. The selection restore routes through the twice-enforced MCP gating invariant ([streaming client §5.3](streaming-client.md#53-the-mcp-gating-invariant-enforced-twice)), so a selection that has gone stale cannot produce the server's 400.

There is no stop endpoint and none is called: aborting the fetch is what ends the turn server-side, and the server still records what the stopped turn spent.

## 7. The transcript is session state

Settled entries — including each assistant entry's final snapshot **and** timeline — live in `TurnStore` and nowhere else. That is a design position, not a gap:

- The stream carries the activity timeline live and transcribes only the answer ([contract §6.2](streaming-contract.md#62-only-the-answer-is-transcribed)). The timeline survives for the session *precisely because* the entries hold it, and is gone on reload by the same fact.
- Reopening a conversation replays nothing yet — **history replay is US-410**, which will load the transcribed messages (answers without their timelines) from the API.
- Any change of conversation — another sidebar row, "New conversation", a deep link — aborts a live turn and clears the transcript, the stopped card, and every notice (US-407: "gone on reopen or route change"). Sign-out does the same through `withResetOnSignOut` plus an explicit abort, the repo's per-user state hygiene: clearing state is not cancelling work.

Anything a user must keep from a stopped or cut-off turn goes through the stopped card's Copy — nothing else preserves it.

## 8. Rendering the turn

[`Transcript`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts) is the conversation column: settled entries first, then whichever live region applies — the thinking ridgeline, the streaming turn, a failure notice, or the stopped card. Its accessibility posture is deliberate:

- `aria-busy` rides the container for the length of a turn, so assistive tech treats the region as settling rather than announcing every re-render. The full streaming live-region treatment is US-1402's.
- A **persistent** visually-hidden `role="status"` region announces abnormal endings only — stopped, failed, cut off. It exists from the first render because a live region created together with its content is not reliably announced, and it empties during every turn so a retry that cuts off *again* transitions the content and is announced a second time.

[`AssistantTurn`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts) renders the avatar column and, in arrival order, the turn's text blocks and activity cards — the render-time join of §4. The blinking caret belongs to the last node, and only when it is a text block. Text renders as **plain pre-wrapped text**: markdown is US-601 (`ngx-markdown` is installed but unimported, by the bundle-budget decision), and rendering it here now would put an unsanitized surface on screen.

[`ActivityCard`](../../enterprise-gpt-ui/src/app/features/chat/transcript/activity-card.ts) is frame `1f`: the activity's `displayName` as the label, the kind as a **separate** badge — the contract keeps them apart so no card ever reads "Calling Jira Cloud MCP" — the `source` subtitle verbatim (the "Atlassian · host" composition is the server's, arriving as one string), sub-status bullets, and a running ring / completed check / failed triangle with durations in mono (`3.1s`, absent until completion). No chevron and no expansion yet — that affordance arrives with US-504's usage strip. [`KindBadge`](../../enterprise-gpt-ui/src/app/shared/badge/kind-badge/kind-badge.ts) is pinned to exactly four labels, one per `ToolKind` arm — Function, MCP tool, Agent, and **Reasoning** for `Unknown`, the kind reasoning phases stream under (previously mislabelled "Tool"; US-501 completed the set).

[`TurnNoticeCard`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-notice-card.ts) carries the turn-edge notices: the cut-off warning (US-406) and the pre-stream failure cards — of which the tool-server-unavailable variant is fully designed today, retiring US-403's deferred criterion, naming the unreachable server when the error carries it. A busy conversation renders frame `1h`'s single hourglass row — no spinner, because nothing is being awaited — and everything else falls back to a generic warning with the `traceId` line, until US-408 (busy) and US-412 (consent) bring their own treatments. Retry on a notice is the *caller's* knowledge, not the card's: `Transcript` offers it only where the store holds a retry and the error arm is retryable.

## 9. The composer and the empty state

The Send and Stop controls are **one persistent button** that morphs with `inFlight` — the element survives the swap, so keyboard focus does too. One settle case still strands focus: a turn settling into an empty composer re-disables the morphed Send while it is focused, and engines disagree about what happens next (some drop focus to `<body>`, others leave it on the disabled control) — both arms are caught, and focus is handed to the textarea. Only a fall-through is corrected; a user focused elsewhere is left alone.

Composer text seeding flows **one way, through the store**: `seedComposer` writes a one-shot seed (a fresh object per seed, so seeding the same text twice still propagates), the composer applies it, focuses the textarea, and acknowledges through `consumeComposerSeed`. Three producers use it — the suggested prompt chips, Stop-during-create restoring the prompt, and the Retry restores — and `protectedState` stays intact throughout.

The empty state (frame `1a`) closes US-303's deferral: the four suggested prompt chips — *Summarize a document*, *Draft a status update*, *Find open Jira blockers*, *Explain a code change* — render on the landing screen only, and a chip's entire behaviour is seeding the composer's text. No focus is moved on arrival, because this is the application's landing screen rather than a state transition, and taking focus would jump a keyboard user past the shell's skip link on every cold start.

## 10. Testing

`TurnStore`'s specs drive the send pipeline with a substituted `ConversationStreamClient` and settle every row of §5's table, including the races — `Finished` against Stop, a route change against a live create, a batch against a reset. The timeline reducer is framework-free and tests in Node beside the codecs ([`turn-timeline.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.spec.ts)); the transcript and composer specs cover the join, the caret, the announcements, and the focus fixups. The feature landed with the suite at 817 specs, all green.

## 11. Key files

| Concern | File |
|---|---|
| Turn store — phases, transcript, settles | [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) |
| Arrival-order index | [`domain/stream/turn-timeline.ts`](../../enterprise-gpt-ui/src/app/domain/stream/turn-timeline.ts) |
| Transcript container, live announcements | [`features/chat/transcript/transcript.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts) |
| Assistant turn — the render-time join | [`features/chat/transcript/assistant-turn.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts) |
| Activity card, kind badge | [`features/chat/transcript/activity-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/activity-card.ts), [`shared/badge/kind-badge/kind-badge.ts`](../../enterprise-gpt-ui/src/app/shared/badge/kind-badge/kind-badge.ts) |
| Turn-edge notices | [`features/chat/transcript/turn-notice-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/turn-notice-card.ts) |
| Stopped card | [`features/chat/transcript/stopped-card.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/stopped-card.ts) |
| Composer — morphing button, seeding | [`features/chat/composer/composer.ts`](../../enterprise-gpt-ui/src/app/features/chat/composer/composer.ts) |
| Empty state and chips | [`features/chat/chat-empty-state.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat-empty-state.ts) |
| Chat screen — providers, route binding | [`features/chat/chat.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.ts) |
| Related reference | [Conversation Streaming Contract](streaming-contract.md), [Conversation Streaming Client](streaming-client.md), [Shell & Navigation](../ui/shell-and-navigation.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
