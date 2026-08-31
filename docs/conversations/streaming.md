# Streaming

The wire contract of `POST api/conversations/{id}/stream`, and the client that reads it.

## Transport

Server-sent events over a `POST` body, which is why the client uses raw `fetch` rather than
`EventSource`.

| Header | Value | Why |
| --- | --- | --- |
| `Content-Type` | `text/event-stream` | |
| `Cache-Control` | `no-cache` | |
| `X-Accel-Buffering` | `no` | Reverse proxies buffer by default, which would hold every frame until the turn ends. Activity that arrives all at once, after the answer, is worse than not streaming it. |

`Connection` is deliberately absent — it is hop-by-hop, owned by Kestrel, and invalid under HTTP/2.

**All three headers are written on the first frame, not up front.** Everything that can fail a turn
— acquiring the conversation lock, reading the transcript, resolving the model, leasing MCP servers
— happens before the first yield, so setting headers eagerly would put streaming headers on a 409
or a 404 and its JSON body. This is also why `TypedResults.ServerSentEvents` is unusable here: it
commits the response before enumerating, so a client would get `200 OK` and only then discover the
turn never started.

A turn that produces nothing still gets the headers, so a reader always sees a well-formed empty
stream rather than a bare `200` it cannot interpret.

### Framing

One event per frame, and nothing else:

```text
data: {"kind":"TextDelta","depth":0,"toolKind":"Unknown","text":"Hello"}\n\n
```

- **No event name.** Every frame goes on the default channel; the client switches on the payload's
  own `kind`. A named channel would put the same information in two places.
- **No `id:`, no `retry:`.** The stream is not resumable — a turn is a single transaction and a
  single Cosmos append, not a cursor. Reconnecting starts a new turn.
- **Exactly one `data:` line per frame.** Serialized JSON never contains a raw newline, so neither
  side needs continuation handling.
- **Frames are separated by a blank line** and flushed individually. A client must buffer across
  `read()` boundaries, because a chunk can end mid-frame.

JSON is produced by the package's source-generated serializer context, which pins the wire format to
camelCase keys, string enum values (`"McpTool"`, not `2`) and omitted nulls. Serializing with
ambient `JsonSerializerOptions` would silently drift the wire from the shipped TypeScript interface.

## Event kinds

| `kind` | Meaning | Carries |
| --- | --- | --- |
| `Status` | A request-level status line | `message` |
| `ActivityStarted` | A tool, MCP call or agent began | `scopeId`, `parentScopeId`, `displayName`, `source`, `toolKind`, `depth` |
| `ActivityProgress` | A sub-status from inside a running activity | `scopeId`, `message`, `progress`, `progressTotal` |
| `ActivityCompleted` | That activity returned | `scopeId`, `durationSeconds` |
| `ActivityFailed` | That activity threw | `scopeId`, `durationSeconds` |
| `TextDelta` | A chunk of the answer | `text` |
| `ReasoningDelta` | A chunk of reasoning text | `text` |
| `Finished` | The turn ran to completion | `usage`, `durationSeconds`, `metadata` |

Every field except `kind`, `depth` and `timestamp` is omitted entirely when null.

Four rules that are easy to get wrong:

- **`ReasoningDelta` is display-only** and is never written to the transcript. Replaying a model's
  own reasoning back to it is neither useful nor what providers expect. Only `TextDelta` is
  transcribed.
- **The first `ReasoningDelta` of every turn is server-authored** — a fixed sentence seeded ahead of
  the model's first event, with a trailing blank line separating it from a model's own first delta.
  So a client cannot infer "this model streams reasoning" from the field's presence. A reasoning
  region should suppress the seeded sentence and render only what follows, and must not assume more
  will come.
- **`Finished` is not a terminator.** It arrives only when the turn ran to completion. End of body
  is the terminator; `Finished` is the signal that the turn ended *well*, and the only place a
  client gets total token usage.
- **`timestamp` is meaningful only for status and activity events.** Deltas and `Finished` leave it
  at its default. Consume events in stream order, never sorted by it.

### `displayName` depends on `toolKind`

| `toolKind` | `displayName` | `source` |
| --- | --- | --- |
| `McpTool` | The raw MCP tool name, `{sanitizedServer}_{tool}` — `Weather_get_forecast` | The catalog server name — `Weather` |
| `Function` | The function's own identifier (`search_documents`) | absent |
| `Agent` | The agent's display name (`Forecast Agent`) | The agent's identifier |

A client is expected to undo the prefix. `source` is the exact string the prefix was built from —
`McpToolProvider` builds it with `SanitizeToolNamePrefix(server.Name)` and passes that same
`server.Name` to the tracking wrapper, deliberately rather than using the server's self-advertised
name, which need not agree. `domain/stream/activity-label.ts` sanitizes `source` the same way,
strips a leading `{sanitized}_`, converts underscores to spaces and upper-cases the first character,
so `Weather_get_forecast` + `Weather` renders as **Get forecast** over **Weather**. Every branch
that cannot prove the prefix leaves the name alone.

The event's `depth` is a **display** depth counting the request as level 0. It is not the `Depth`
column in the audit trail, which counts nesting only — a tool the assistant called directly is `1`
on the wire and `0` in the database.

### `Finished` carries the persisted message id

`metadata` carries exactly one key, `assistantMessageId`, the transcript id of the message this turn
wrote. The key name is an **opaque identifier clients match verbatim**; renaming it is a breaking
API change.

The frame is **held back, not forwarded live**. The middleware's `Finished` is intercepted, kept out
of the sequence, and re-yielded only after the transcript write has answered. Two consequences
follow structurally rather than by convention: `Finished` is always the true last frame of a
completed turn, and the identifier is claimed only when the transcript actually holds it.

| Turn outcome | `Finished` sent | `metadata` |
| --- | --- | --- |
| Completed, write succeeded | yes | `{ "assistantMessageId": "<guid>" }` |
| Completed, write failed or threw | yes | absent |
| Cancelled or faulted | no | — |

A write that throws is the case worth spelling out: the failure is captured with
`ExceptionDispatchInfo`, the metadata-less `Finished` is yielded first, and only then is the
exception rethrown — after the frame, never instead of it. A turn that ran to completion must not be
downgraded into one the client can only read as truncated.

## Errors

**Before the first frame** — the ordinary problem-details surface:

| Status | `type` | When |
| --- | --- | --- |
| 400 | `validation-error` | Body failed validation |
| 403 | `forbidden` | The caller may not use something the turn needs |
| 404 | `resource-not-found` | Conversation unknown, deactivated, or another user's |
| 409 | `conversation-busy` | A turn is already in flight for this conversation |
| 428 | `mcp-credential-required` | A selected server takes a user-supplied API key and none is stored |
| 428 | `mcp-credential-rejected` | A selected server refused the stored key |
| 502 | `mcp-server-unavailable` | A selected server was unreachable, or its on-behalf-of token could not be acquired |
| 503 | `provider-not-configured` | The model exists but this deployment has no client for its provider |

**After the first frame** — there is no error surface left. The exception handlers short-circuit on
`Response.HasStarted` rather than corrupting a stream that is already `200 OK` with half an answer
in it, so a mid-stream failure reaches the client as **a body that simply stops**. A client cannot
distinguish that from a network drop and should not try: treat an ended body with no `Finished` as
an incomplete turn and say so.

The boundary is the synthetic opening pair. Those two frames commit the response to
`text/event-stream` *before* the provider is called, so a provider that faults on its very first
token surfaces as mid-stream truncation rather than a problem body. That is the deliberate price of
letting the client hear something during model latency.

## A turn, frame by frame

```json
{"kind":"Status","depth":0,"toolKind":"Unknown","message":"Starting","timestamp":"2026-08-08T10:14:01.874+00:00"}
{"kind":"ReasoningDelta","depth":0,"toolKind":"Unknown","text":"Reviewing the request and preparing a response.\n\n"}
{"kind":"ActivityStarted","scopeId":"s1","depth":1,"toolKind":"McpTool","displayName":"Weather_get_forecast","source":"Weather","timestamp":"2026-08-08T10:14:02.117+00:00"}
{"kind":"ActivityProgress","scopeId":"s1","depth":2,"toolKind":"McpTool","message":"Fetching forecasts","progress":3,"progressTotal":7,"timestamp":"2026-08-08T10:14:03.402+00:00"}
{"kind":"ActivityCompleted","scopeId":"s1","depth":1,"toolKind":"McpTool","durationSeconds":2.41,"timestamp":"2026-08-08T10:14:04.531+00:00"}
{"kind":"TextDelta","depth":0,"toolKind":"Unknown","text":"It will be "}
{"kind":"TextDelta","depth":0,"toolKind":"Unknown","text":"sunny."}
{"kind":"Finished","depth":0,"toolKind":"Unknown","durationSeconds":5.02,"usage":{"inputTokens":812,"outputTokens":146,"totalTokens":958},"metadata":{"assistantMessageId":"5b7e2f1a-9c3d-4e6b-8f21-0a1b2c3d4e5f"}}
```

Note what is **not** there: no prompt, no tool arguments, no tool results.

## The client

Three layers, following the app's own dependency direction:

| Layer | Owns | Where |
| --- | --- | --- |
| Codec | Body bytes to `AssistantUiEvent[]`, correct across arbitrary chunk boundaries | `domain/stream/` — framework-free, tests in Node with no `TestBed` |
| Transport | The request, auth, error typing, abort, batching | `core/stream/conversation-stream-client.ts` |
| Turn state | Folding events into a rendered turn | `features/chat/turn-store.ts` |

`StreamCodec` is `decode(chunk: Uint8Array): AssistantUiEvent[]` plus `flush()` — synchronous and
push-based, with **one instance per request**, because both implementations carry cross-read state
(a partial frame, or an incomplete multi-byte character).

- `sse-frame-codec.ts` is the framed decoder. Its `KNOWN_EVENT_KINDS` table is
  `as const satisfies Record<AssistantUiEventKind, true>`, so a contract upgrade adding a ninth kind
  fails compilation rather than silently dropping events in production.
- `raw-text-codec.ts` is the `features.rawStreamCodec` fallback for servers predating the framed
  contract; each chunk becomes one synthesized `TextDelta` so everything downstream is identical.

`domain/stream/andes/assistant-ui.contract.ts` is **vendored and must not be edited** — a
byte-for-byte copy of the package's TypeScript declaration, verified by `npm run check:contract`.

Two reducers run over the same events and answer different questions. The **fold**
(`foldAssistantEvents`) is hierarchical and unordered relative to text; the **timeline**
(`turn-timeline.ts`) is arrival-ordered, where a node is either a half-open `[start, end)` slice
into the folded snapshot's single `text` string or an `activity` reference. The timeline never
copies text, so a delta is O(1) regardless of transcript length.

`replayed-turn.ts` pushes a persisted assistant message back through those same reducers, so
replayed and live answers render through one code path.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs` | Yields the event sequence, holds back `Finished` |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Endpoints/ConversationEndpoints.cs` | `StreamConversationAsync` and its lazy headers |
| `enterprise-gpt-ui/src/app/domain/stream/sse-frame-codec.ts` | Frame decoding across chunk boundaries |
| `enterprise-gpt-ui/src/app/domain/stream/turn-timeline.ts` | Arrival-ordered timeline |
| `enterprise-gpt-ui/src/app/domain/stream/activity-label.ts` | Prefix stripping for MCP tool names |
| `enterprise-gpt-ui/src/app/core/stream/conversation-stream-client.ts` | Raw-fetch transport |
| `enterprise-gpt-ui/src/app/domain/stream/sse-frame-codec.spec.ts` | Chunk-boundary cases |

## Related

- [turn-lifecycle.md](turn-lifecycle.md)
- [transcripts.md](transcripts.md)
- [usage-and-reporting.md](usage-and-reporting.md)
