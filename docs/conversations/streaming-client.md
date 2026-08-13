# Conversation Streaming Client

The frontend half of the [Conversation Streaming Contract](streaming-contract.md): what `enterprise-gpt-ui/` does with the bytes that document specifies, from the `fetch` that carries a turn to the selection state that decides what the request says. Audience: whoever builds on this pipeline or debugs a turn that ended strangely. The layer above it — sending, the live turn, Stop, the chronological timeline (US-401, US-406, US-407, US-501) — has since shipped and is documented in the [Conversation Turn Lifecycle](turn-lifecycle.md); resuming a conversation's settings (US-410) is still to come. The wire format is deliberately not restated here; wherever a behaviour exists because the wire demands it, the contract section is linked instead.

## 1. Overview

Three layers, shipped by four stories (US-402–US-405), with dependencies running one way like the rest of the app (`features → core → domain`):

| Layer | What it owns | Where | Story |
|---|---|---|---|
| Codec | Body bytes → `AssistantUiEvent[]`, correctly across arbitrary chunk boundaries | `domain/stream/` — framework-free, so it tests in Node with no `TestBed` | US-404 |
| Transport | The request, auth, error typing, abort, batching | `core/stream/` — `ConversationStreamClient` | US-405 |
| Turn settings | Which model and which MCP servers the next turn will use | `core/chat/` — `TurnSettingsStore`, rendered by the composer (`features/chat/composer/`) | US-402, US-403 |

US-401 has since wired the send: [`TurnStore`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) spreads `streamSelection` into the transport request and is the consumer everything below was built for — the pipeline was built and tested from the bottom so that story was assembly, not invention. What the store does with the batches is the [Conversation Turn Lifecycle](turn-lifecycle.md)'s subject; this document stays on the layers beneath it.

## 2. Quick start

What any consumer of the transport looks like — the real one is `TurnStore`'s send pipeline ([turn lifecycle §3](turn-lifecycle.md#3-sending-and-creating-the-conversation-around-the-first-prompt)):

```typescript
import { createInitialSnapshot, foldAssistantEvents } from '@domain/stream/andes/assistant-ui.contract';
import { AppError } from '@core/errors/app-error';
import { ConversationStreamClient } from '@core/stream/conversation-stream-client';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';

const client = inject(ConversationStreamClient);
const settings = inject(TurnSettingsStore);

const selection = settings.streamSelection();
if (selection === null) return; // no model resolved — send is disabled in this state (§5.2)

let snapshot = createInitialSnapshot();
client.stream({ conversationId, prompt, ...selection }).subscribe({
  // Batches, not single events: at most one flush per ~16ms, so one fold —
  // and in a store, one patchState — per animation frame at any token rate.
  next: (batch) => {
    snapshot = batch.reduce(foldAssistantEvents, snapshot);
  },
  // Always an AppError. The 'aborted' arm is Stop, sign-out, or unsubscribe;
  // every other arm is a pre-stream failure with an ordinary problem body.
  error: (error: AppError) => {},
  // The body ended. If no Finished event was folded, the turn was cut off —
  // that detection is US-406's, not the transport's (§4.3).
  complete: () => {},
});
```

## 3. The codec layer

### 3.1 `StreamCodec` — synchronous push decoding

Both codecs implement one interface ([`stream-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/stream-codec.ts)): `decode(chunk: Uint8Array): AssistantUiEvent[]` per body read, then `flush(): AssistantUiEvent[]` exactly once at end-of-body. Synchronous by design — byte-to-event decoding has no async work in it, and a pure push API keeps chunk-boundary fixtures trivial to assert against ("after chunk 1, nothing; after chunk 2, one event"). Both codecs carry state across reads — a partial frame, or an incomplete multi-byte character — so an instance serves exactly one request and is never shared between streams.

### 3.2 The SSE frame codec

[`createSseFrameCodec()`](../../enterprise-gpt-ui/src/app/domain/stream/sse-frame-codec.ts) decodes the framed stream — `data: {json}\n\n`, one event per frame, no `event:`/`id:` lines ([contract §3.2](streaming-contract.md#32-framing-rules)). Four behaviours matter to anyone reading its output:

- **A chunk can end anywhere** — mid-frame, mid-JSON, mid-character — so a carry-over buffer holds the partial tail across reads, and the `TextDecoder` runs in streaming mode so a multi-byte character split across two chunks never becomes U+FFFD.
- **Unknown `kind`s are skipped through an exhaustive table.** The eight known kinds live in a `satisfies Record<AssistantUiEventKind, true>` map, exhaustive in both directions: a contract upgrade that adds a ninth kind fails compilation here, forcing a decision instead of silently dropping the new events in production. At runtime, a kind the build does not know is the documented forward-compatibility path and drops without ceremony.
- **Malformed frames are skipped, not thrown.** The wire has no error surface after the first frame ([contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame)), so throwing would destroy the rest of a live turn's output over one defective frame.
- **`flush()` salvages a terminator-less final frame.** A final frame that lost only its trailing blank line to truncation is still a complete event and is recovered; a tail cut mid-JSON parses as nothing and drops — which is precisely the "body just stops" ending the contract says a client must tolerate.

### 3.3 The raw-text fallback — `features.rawStreamCodec`

[`createRawTextCodec()`](../../enterprise-gpt-ui/src/app/domain/stream/raw-text-codec.ts) exists for a deployment still running a server that predates the framed contract: the body is unframed answer text, and each chunk becomes one synthesized `TextDelta`. The transport selects between the two codecs with a single expression on the `features.rawStreamCodec` flag in `config.json` — everything downstream of the transport (the fold, the turn state, the transcript) is identical for both codecs; only the byte-level decoding differs. Synthesized events carry `DEFAULT_EVENT_TIMESTAMP`, the .NET default `DateTimeOffset` — recognizably "unset", and deterministic under test where a clock read would not be. The contract forbids ordering by timestamp, so no consumer may attach meaning to it.

### 3.4 The fold is vendored, pinned, and never reimplemented

The codec produces events; folding them into a renderable snapshot is the job of `foldAssistantEvents`, vendored byte-for-byte from the `Andes.Extensions.AI.UI` package at [`domain/stream/andes/assistant-ui.contract.ts`](../../enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.contract.ts) and drift-checked by `npm run check:contract` ([contract §5.2](streaming-contract.md#52-do-not-write-the-reducer--the-package-ships-it)). US-404 added [`assistant-ui.fold.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.fold.spec.ts), which pins the fold semantics for all eight event kinds — nesting under a parent scope at any depth, orphan promotion to a new root, unknown-kind tolerance — against the vendored function itself, never a reimplementation. The vendored file stays byte-identical; the spec is what makes a package upgrade that changes fold behaviour visible.

## 4. The transport

[`ConversationStreamClient.stream(request)`](../../enterprise-gpt-ui/src/app/core/stream/conversation-stream-client.ts) returns `Observable<readonly AssistantUiEvent[]>` — batches, not single events — for `POST api/conversations/{id}/stream`. It is a raw `fetch`, because `HttpClient` cannot expose a readable body stream; the bearer token therefore comes from the same `TokenService` the `authInterceptor` uses, and the `fetch` itself is injected through the `STREAM_FETCH` token so specs substitute a fake by provider override rather than a global stub. The request body always carries `mcpServers: [{ id }]` — `[]` when empty, never absent, which is the field the deleted client dropped entirely.

How a turn can end, and what the observable does:

| Ending | The observable | How a consumer tells |
|---|---|---|
| Turn ran to completion | batches, then `complete` | A `Finished` event was folded |
| Body stopped without `Finished` — mid-stream fault or network drop | batches (including the codec's salvaged tail), then `complete` | No `Finished` folded — US-406's "cut off" detection |
| Pre-stream failure — non-2xx, transport error, bad token | `error(AppError)` | Match on the arm; the problem body is already parsed and typed |
| Abort — Stop, sign-out, or unsubscribe | `error({ kind: 'aborted' })` | The `aborted` arm; every batch already flushed was delivered first |

### 4.1 Errors first, typed always

The server sets its `text/event-stream` headers lazily on the first frame, so every pre-stream failure carries ordinary `application/problem+json` ([contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame)). The transport honours that: on `!response.ok` the problem body is parsed through `toAppErrorFromResponse` **before a single body byte is read as stream**, and the observable errors with the result. Every value the observable ever errors with is an `AppError` — that is documented as the class's contract, so no consumer needs a second normalization layer.

A bare 401 is replayed exactly once with a force-refreshed token, gated by `authErrorDecision` — the same single source of truth the `HttpClient` interceptor consults. Without the replay, a token the API had just rejected would kill a turn that every ordinary request would have survived; routing the decision through the shared table structurally keeps the 403 `mcp-authorization-required` out of the refresh path, since no refresh can fix a consent requirement. The replay is safe because authentication is rejected before the conversation lock is taken, so it cannot double-start a turn.

### 4.2 Aborting — Stop, sign-out, unsubscribe

Three cancellation sources compose into one signal: `AbortSignal.any([teardown, signedOut, caller.signal])`. The teardown controller aborts when the subscription ends, so a `switchMap`-style consumer cancels the network request rather than orphaning it; the signed-out signal comes from `core/events/session-events`, honouring the repo rule that clearing state is not cancelling work; and `request.signal` is the caller's — US-407's Stop button. All three surface identically: the observable errors with the `aborted` arm, the race-free discriminator that keeps US-406's "completed without `Finished` → cut off" logic from firing on every Stop. Every batch flushed before the abort was already delivered, so partial output survives — which is US-407's acceptance criterion, met from below. There is no stop endpoint and none is called; aborting the fetch is what ends the turn server-side, and the server still records what it spent.

### 4.3 Faults after the body began complete gracefully

Once the body has started there is no error surface left on the wire — a mid-stream fault reaches the client as a body that simply stops, indistinguishable from a network drop ([contract §3.3](streaming-contract.md#33-errors-before-and-after-the-first-frame)). The transport does not manufacture a distinction the wire cannot support: a read that throws after the first byte flushes the codec's buffered tail — so the two "body just stops" endings deliver the same text — and **completes** the observable rather than erroring it. Both endings take the single path US-406 already owns: a body that ended without `Finished`.

### 4.4 Batching

Decoded events are buffered with `bufferTime(16)` and a non-empty filter (`STREAM_BATCH_WINDOW_MS`), so a consumer folds one batch — one `patchState`, in a store — per flush, at most once per animation frame at any token rate. `bufferTime` rather than `requestAnimationFrame` because it is deterministic under Vitest's fake timers without a browser scheduler. Empty batches are never emitted, and the residual buffer is flushed on completion.

## 5. Turn settings and the composer

### 5.1 Null means "follow the default"

[`TurnSettingsStore`](../../enterprise-gpt-ui/src/app/core/chat/turn-settings-store.ts) is root-provided — the selection persists across conversations for the session — and holds `selectedModelId: string | null`, where null means *follow the catalog's default*. That representation is what makes preselection declarative: the composer snaps to the `isDefault` model the moment the late-loading catalog arrives, a catalog reload that removes an explicitly selected model falls back to the new default instead of stranding a ghost id, and the sign-out reset (`withResetOnSignOut`, composed last as always) restores follow-default for the next session. No seeding effect exists anywhere.

### 5.2 `streamSelection` — the app never sends blind

`streamSelection` is the one artifact US-401 spreads into the transport request: `{ modelId, mcpServerIds }`, or **null while no model resolves** — covering a pending catalog, a failed catalog, and an empty one alike. The composer disables send on null (plus empty text), so a request without a `modelId` is unrepresentable rather than merely avoided (frame `2j`). When the catalog fails, the model pill reads "Models unavailable", an amber line above the control row explains that sending is disabled, and the menu itself offers Retry — catalog failure deliberately wins over the cleared-tools warning, because with no model there will be no turn and the tools state is moot.

### 5.3 The MCP gating invariant, enforced twice

A model whose `isToolEnabled` is false must never reach the wire with MCP servers attached — the server answers that with a 400. The invariant lives in two places on purpose:

1. **`selectModel` performs the visible clear.** Selecting a non-tool model while servers are selected empties the selection and records which model did it, so the composer can render frame `2d`'s warning — "*{model}* can't call tools — your previous selection was cleared" — sticky until the model changes again. Masking instead of clearing would resurrect the selection on the next tool-capable model, contradicting "was cleared".
2. **`effectiveMcpServerIds` re-derives emptiness.** The wire-facing set is computed: empty whenever the selected model cannot call tools, and always intersected with the permitted catalog. So even a path that bypasses `selectModel` — US-410 seeding a conversation's stale settings, a catalog reload dropping a server mid-session — cannot produce the server's 400.

The Tools pill label ("Tools", "1 Tool", "N Tools") reads off the effective set, so it never advertises servers that will not be sent, and `applyConversationSettings` — deliberately unvalidated, because the computed joins absorb ids the catalogs no longer carry — is the seeding surface US-410 will call when reopening a conversation restores the model and servers it last used.

### 5.4 Provider presentation

`ModelDto` carries only `providerId`, and no endpoint exposes provider names to a non-admin caller — so [`domain/api/provider.ts`](../../enterprise-gpt-ui/src/app/domain/api/provider.ts) mirrors the three contract-stable seeded provider ids (Azure OpenAI, Amazon Bedrock, Anthropic; `Providers.cs` documents them as matching the seeded rows verbatim) onto a display name and a status-dot tone, backed by three new `--provider-*` tokens. An id this build does not know degrades to a muted dot and a caption without the provider segment — a new provider seeded server-side must not break the picker.

### 5.5 The composer, and what it deliberately lacks

The composer (frames `2b`–`2j`) is the textarea, the warning line, and the control row: model pill, tools pill, send. The attach, project, mic, and download controls are **absent, not disabled**, until their stories land — the repo's pattern for unshipped affordances — and send is `[disabled]`-bound to its real conditions but has no click handler until US-401. The tools menu rows are `menuitemcheckbox` items with icon-glyph checks, because a native checkbox input is not a valid child of `role="menu"`; the panel stays open while toggling, and Escape, Tab, outside-press, and scroll still dismiss. Both pickers ride the shared `Menu`, extended additively for this work (projected trigger face, `stayOpen`, aria-only `disabled`, `panelWidth`, bottom-anchored `direction='up'` so content growth extends upward, and focus recovery when a content swap unmounts the focused item) so the existing kebab consumers are untouched.

Two deliberate deviations from the design, agreed 2026-08-12 and recorded in the PRD:

- **The tools rows carry the server name only.** `McpDto` is `{ id, name, description }` — no key field exists to fill frame `2c`'s mono column, so it is omitted rather than fabricated from the name. A backend enabler can add a real key later.
- **The tool-server-unavailable panel (US-403 criterion 4) was deferred to US-406/407 — and shipped there, retiring the deferral.** Its non-visual half — the `mcp-server-unavailable` error arm, server-name extraction, retry policy — shipped tested in US-103; the card itself is one of the sibling turn-edge-state cards that belonged with the transcript that hosts them, and `TurnNoticeCard` now renders it ([turn lifecycle §8](turn-lifecycle.md#8-rendering-the-turn)).

## 6. What the stories above consume

US-401, US-406 and US-407 have shipped; the table records what each took from this layer, and the [Conversation Turn Lifecycle](turn-lifecycle.md) documents what they built with it. US-410 is still open.

| Story | Consumes |
|---|---|
| US-401 — send a first prompt (shipped) | `streamSelection` spread into `StreamTurnRequest`; the send button's disable conditions; `ConversationStreamClient.stream` |
| US-406 — watch the answer arrive (shipped) | The batch observable, folded with the vendored `foldAssistantEvents`; owns the "completed without `Finished`" detection (§4.3) |
| US-407 — Stop (shipped) | `request.signal`; the `aborted` arm as the discriminator; delivered-batch semantics that keep partial output (§4.2) |
| US-410 — resume settings | `applyConversationSettings`; the twice-enforced gating invariant absorbing stale ids (§5.3) — the stopped card's Retry already exercises the same seam |

## 7. Testing

The codec layer is framework-free and tests in Node with no `TestBed`; its chunk-boundary fixtures (a frame split mid-JSON, mid-character, a truncated tail) live beside the codecs, with shared event builders in `@testing/stream-frames`. The transport's specs substitute `STREAM_FETCH` by provider override and drive `bufferTime` with fake timers — the reason it is `bufferTime` at all (§4.4). The fold spec (§3.4) pins the vendored reducer against recorded fixtures. Catalog fakes for the settings store and composer live in `@testing/catalog`.

## 8. Key files

| Concern | File |
|---|---|
| Codec interface, timestamp sentinel | [`domain/stream/stream-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/stream-codec.ts) |
| SSE frame codec | [`domain/stream/sse-frame-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/sse-frame-codec.ts) |
| Raw-text fallback codec | [`domain/stream/raw-text-codec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/raw-text-codec.ts) |
| Vendored contract and fold | [`domain/stream/andes/assistant-ui.contract.ts`](../../enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.contract.ts), pinned by [`assistant-ui.fold.spec.ts`](../../enterprise-gpt-ui/src/app/domain/stream/andes/assistant-ui.fold.spec.ts) |
| Transport | [`core/stream/conversation-stream-client.ts`](../../enterprise-gpt-ui/src/app/core/stream/conversation-stream-client.ts), [`core/stream/stream-fetch.token.ts`](../../enterprise-gpt-ui/src/app/core/stream/stream-fetch.token.ts) |
| Turn settings | [`core/chat/turn-settings-store.ts`](../../enterprise-gpt-ui/src/app/core/chat/turn-settings-store.ts) |
| Provider presentation | [`domain/api/provider.ts`](../../enterprise-gpt-ui/src/app/domain/api/provider.ts) |
| Composer and pickers | [`features/chat/composer/`](../../enterprise-gpt-ui/src/app/features/chat/composer/composer.ts) |
| Related reference | [Conversation Streaming Contract](streaming-contract.md), [Conversation Turn Lifecycle](turn-lifecycle.md) (the layer above: `TurnStore`, settles, the timeline), [Frontend Foundation](../ui/frontend-foundation.md) (the `AppError` taxonomy, `authErrorDecision`), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md) |
