# Turn Lifecycle

What happens between pressing Send and a settled transcript entry, and what happens when a
conversation is reopened. The wire format is in [streaming.md](streaming.md); this doc is the state
around it.

## The state machine

`TurnPhase` is one value, and everything the screen shows during a turn derives from it.

| Phase | Meaning | Shows |
| --- | --- | --- |
| `idle` | No turn in flight | Send control; transcript or empty state |
| `creating` | `POST api/conversations` is out for a first prompt | The thinking ridgeline |
| `awaitingFirst` | The stream request is out; nothing renderable has arrived | Ridgeline, or the reasoning region once the model reasons |
| `streaming` | Content is arriving | The live turn, with the blinking caret |
| `stopping` | Stop was pressed; the abort's error is on its way | The live turn, until the settle lands |

`inFlight` is anything but `idle`. It is one of two things that morph the composer's button into
Stop — the other is a busy attachment upload, which has no turn open yet but still has something to
cancel; `Composer` combines the two into its own local `inFlight`.
`showThinking` and `showLiveTurn` split on whether the model is reasoning, so during
`awaitingFirst` exactly one is true: the pre-answer gap belongs to the ridgeline on turns where the
model reasons silently, and is handed to the reasoning region the moment the model reasons in its
own words — so the summary is readable *as it streams* rather than appearing whole at the first
token.

`awaitingFirst` advances to `streaming` only when a batch carries **renderable** content — an
`ActivityStarted` or a `TextDelta`. A batch of `Status` or `ReasoningDelta` deliberately does not
move the phase: every turn opens with a synthetic `Status` and a server-authored `ReasoningDelta`,
and without this rule those two frames would advance the phase with nothing to show.

Loading a conversation's stored messages is deliberately **not** a phase — `historyPending` and
`historyError` are their own state. No turn is in flight while history loads and the composer stays
usable throughout; folding it into `TurnPhase` would put both facts at risk.

## One session per conversation

A user can start a turn, open another conversation and start a second while the first is still
streaming. Aborting a fetch cancels the turn server-side — the answer is billed but never
transcribed — so leaving a conversation must not end its turn. Each conversation therefore gets a
**chat session**, and the phase above is the phase of one session.

`ChatSessions` (root-scoped, `features/chat/chat-sessions.ts`) keeps one session per conversation.
A session is an environment injector, built with `createEnvironmentInjector`, that owns that
conversation's `TurnStore` and `UploadStore`. The stores live exactly as long as the injector, so a
stream is aborted only when its session is destroyed.

- **`Chat` shows one session at a time.** It calls `sessions.activate(conversationId)` and renders
  `ChatSessionView` (transcript, pinning, composer) through `*ngComponentOutlet` with
  `environmentInjector: session.injector`. The element chain still belongs to `Chat`, so
  `ConversationStore` and the markdown providers stay the screen's; the turn and upload stores come
  from the session. The view is re-created for each session, so composer focus and transcript
  pinning start fresh rather than carrying over from the conversation just left.
- **Sessions are found by `turn.boundConversationId()`.** `/chat` with no id resolves to the newest
  idle session that has none, or makes one.
- **A session that was left is dropped after the next render, but only if it is idle and holds
  nothing unsent** — no draft, no composer seed, no attachment chips. A turn still in flight keeps
  its session. One that settled while out of sight is kept until it is viewed, so a cut-off card, a
  failure notice or a tool timeline is still there when the user comes back.
- **Deleting a conversation destroys its session.** If it is on screen, the destroy waits until the
  view has left. Sign-out destroys every session but the active one, which resets itself. Destroying
  the registry destroys them all.

`TurnStore` takes no route input. `init(id)` runs once when its session is built: it loads the
stored history and claims the project screen's handoff (see [Reopening](#reopening)). `TurnStore`
also no longer injects `ConversationStore`; the composer's project and export targets come from
`ChatComposerHost` (`features/chat/chat-composer-host.ts`), which reads the session's turn and the
screen's conversation.

Each session keeps its own unsent text (`draft` / `setDraft`, part of the `ComposerHost` contract)
and its own attachment chips, because the chips are its own `UploadStore`. Switching conversations
brings back what was typed in each.

## Sending

The send pipeline is one `rxMethod` on `exhaustMap`, which is the double-send guard. A send while a
turn is in flight is **ignored, not queued** — a queued one would only meet the server's 409,
because a conversation runs one turn at a time. `send(prompt)` re-checks what the composer's
disabled button already enforces, so no caller can issue a turn without a `modelId`. It also
refuses at the [concurrent-turn cap](#concurrency).

### The first prompt creates the conversation

"New conversation" creates nothing; no conversation exists until the first prompt is sent. When a
send finds no bound conversation the store:

1. Enters `creating` and posts `api/conversations`.
2. On the 201, prepends the new conversation into `ConversationListStore` so the sidebar row exists
   the moment the URL updates, binds the session's `UploadStore` to the new id, and patches
   `boundConversationId`.
3. Navigates with `router.navigateByUrl('/chat/{id}', { replaceUrl: true })` and streams against the
   new id.

The create **keeps going if the user leaves**. Navigation happens only when all three hold: the
session is still the one `Chat` shows, no navigation is already in progress, and the router is still
on bare `/chat`. Otherwise the conversation is created and streams in the background, and the user
stays where they went; the sidebar row is already there.

The ordering in step 2 is load-bearing. When the URL catches up, `ChatSessions` looks the session up
by `boundConversationId()` and finds this one, so the route change is a no-op rather than a second
session that re-reads history. The id has to be set *before* navigating for that lookup to succeed.

`replaceUrl: true` rather than `Location.replaceState`, because the latter moves the URL without
moving the Router: `routerLinkActive` selection breaks, "New conversation" becomes a same-URL no-op,
and the route's `conversationId` input never binds.

### When the create goes wrong

- **The POST fails** — the prompt goes straight back into the composer and a failure notice renders
  *without* a Retry button, because the reseeded composer is the retry surface. If the session is
  not on screen nobody can see that notice, so the failure also raises an error toast. The session
  holds a composer seed, so it is kept until the user comes back to it.
- **Stop mid-create** — the POST is cancelled and the prompt reseeded. Leaving the conversation is
  not a cancel: the create carries on, as above.
- **Sign-out mid-create** — the same `takeUntil` races `injectSignedOut()`.

### Attachments gate the stream

Between having a conversation id and opening the stream, the pipeline waits on the session's
`UploadStore.settled$()`. Each session has its own `UploadStore`, so another conversation's uploads
can neither hold this gate shut nor release it. A document is only reachable by the model once
ingestion has written its chunks, and `document_search` is attached to a turn from what the
conversation already holds — a stream opened while a file is still uploading would ground the answer
in a document it cannot see yet. The composer defers uploading until Send
(`UploadStore.deferUploadsUntilSend()`, so attaching only states an intention), which is why the
wait always has something to do: `send()` calls `UploadStore.startPending()` first, posting whatever
the composer was holding, and only then waits for it to settle. A turn with no attachments takes the
synchronous path and pays nothing.

Stop and sign-out end the wait without opening a stream — the same two that end `creating` above;
leaving the conversation does not. Stop only ends the *wait*, though; calling off the uploads
themselves is the caller's job, and the order is load-bearing: `turn.stop()` first, then
`uploads.cancelAll()`, because cancelling the uploads first would settle the gate synchronously and
release the waiting turn into the stream a moment before `stop()` had ended the wait itself —
turning a clean pre-stream cancel into a stopped-turn card with the prompt already gone. The
composer's Stop control and `ChatSessions.stop()` (used by the sidebar) both keep that order.

Once a turn has ended, the session's ready chips are removed. A chip left behind would make every
later prompt look attached, and retrieval reaches the document through the conversation anyway. The
cleanup waits for the turn to end, so a chip is not pulled from under the note explaining why Send
is waiting.

## Settling

The transport ends a turn in exactly two ways, `complete` or `error(AppError)`, and the store
discriminates each against the phase and the folded snapshot.

| Stream ending | Phase at settle | Outcome |
| --- | --- | --- |
| `complete`, `Finished` folded | any in-flight phase, **including `stopping`** | One atomic append of the user and assistant entries; live turn cleared |
| `complete`, no `Finished` | `awaitingFirst` / `streaming` | Cut-off entry keeping snapshot, timeline and a Retry |
| `complete`, no `Finished` | `stopping` | The stopped card — the server closed the body just as Stop was pressed |
| `error` `aborted` | `stopping` | The stopped card, holding the flushed partial text |
| `error` `aborted` | `idle` | Nothing — a reset already cleared the state, and patching would resurrect a turn |
| `error` `aborted` | any other | Live turn cleared only; a phase left in flight would wedge the composer on a Stop control with nothing to stop |
| `error`, other arm | `stopping` | Prompt reseeded, **no** notice — the user wanted out |
| `error`, other arm | `awaitingFirst` / `streaming` | Failure notice, with Retry where the arm is retryable |

Three decisions behind that table:

- **`aborted` is discriminated by phase, never by abort reason.** Stop, sign-out and a destroyed
  session all surface as the same `aborted` arm. What distinguishes them is what the store did
  first: Stop flips the phase to `stopping` before aborting, while sign-out resets to `idle` before
  the rejection lands. A destroyed session has no screen left to settle.
- **`Finished` racing a Stop wins.** The server transcribed the answer, so a card claiming "not
  saved" would be false.
- **The settle is one atomic patch.** The optimistic user bubble is a *field* (`pendingUserText`),
  not a transcript entry, so `Finished` appends both entries in a single `patchState` and Stop
  removes the bubble by nulling a field rather than popping an array.

A body that ends without `Finished` is a deliberate **non-error** arm: a mid-stream fault and a
network drop are indistinguishable on the wire, so the partial answer keeps its place under a "This
response was cut off" warning, with a Retry that re-sends the turn's own prompt and selection as
sent, even if the pickers have moved since.

### What other stores hear

A session's `TurnStore` is out of reach of the root stores, so a settle is announced on the event
bus (see [../frontend/state.md](../frontend/state.md#cross-store-events)):

- **`turnEvents.completed`** — only on a folded `Finished`, with `wasFirstTurn`. A stopped,
  cut-off or failed turn gave the server nothing to save or name.
- **`turnEvents.interrupted`** — a cut-off, or a failure that arrives while the turn is live
  (including one before the stream opens). **Never** for Stop or an abort.

Both feed `ConversationActivityStore`, which marks the conversation when the ending happened out of
sight. After a first turn completes, it also re-reads the conversation to pick up the
server-generated name, so a conversation that finished in the background is named in the sidebar
without being opened.

## Stop

- **During `creating`** — the POST is cancelled and the prompt returns to the composer. Nothing
  streamed, so there is no partial output to card.
- **During `awaitingFirst` or `streaming`** — the phase flips to `stopping` *before* the abort, both
  for the settle discrimination above and because the synchronous patch returns the Send control
  immediately rather than after the rejection's round trip.

The result is a **detached stopped card**: dashed border and left inset, stating that this text is
not part of the transcript. The server transcribed neither the answer nor the prompt, and the card
vanishes on reopen and reload because the state behind it does. It offers Copy, and a Retry that
restores the prompt *and* the turn's model and MCP selection into the pickers without re-sending.

Stop does not need the conversation on screen. A running conversation's sidebar row has a "Stop
response" item, which calls `ChatSessions.stop(id)`: the same `turn.stop()` then `uploads.cancelAll()`
as the composer's control. The session keeps the stopped card, so it is there when the user opens
the conversation.

There is no stop endpoint and none is called: aborting the fetch is what ends the turn server-side.
The server still records what the stopped turn spent.

## Reopening

Replay hangs off the session's one-shot `init(id)`, not the route input, and a replayed answer goes
through the same reducers as a live one — so there is one rendering path, not two.

Points worth knowing:

- **A kept session is not re-read.** Reopening a conversation whose session is still alive shows its
  state as it is — live turn, cut-off cards, notices, tool timelines and all. History is read only
  when a session is built, which is why a session that ended out of sight is worth keeping: the
  stored transcript has none of those.
- **Two id ranges.** Replayed history and live entries use disjoint id ranges, which is what makes
  "replaced, not merged" safe when a reopen races a settle.
- **Replayed history carries no activity timeline** — permanently. Activity events are display-only
  and were never transcribed, so a reopened turn shows its answer without the tool cards it had
  while live.
- **The model and MCP selection are restored** through the same `applyConversationSettings` seam
  Retry uses, and route through the MCP gating invariant so a stale selection cannot produce a 400.
- **The name is server-generated** after a first turn and arrives through `conversationEvents`.
- **An unreadable stored transcript** surfaces as `historyError` with the composer still usable.

## Concurrency

`ConversationLockService` holds one lock per conversation. A second concurrent turn gets
`ConversationBusyException`, which the handler maps to 409 `conversation-busy`. The lock is released
in a `finally` that also persists the turn's outcome, so a turn abandoned by a disconnecting client
is still billed and recorded.

The lock is per conversation, so the client may run **several conversations at once**, one turn
each. What bounds it is connections: every stream holds one open. Over HTTP/1.1 a browser allows six
per host, shared with every other API call, so the client caps concurrent turns at
`chat.maxConcurrentTurns` (default 4, range 1–6; see
[../operations/configuration.md](../operations/configuration.md#the-frontend)).
`ConversationActivityStore.atCapacity` compares the running count — which includes a first prompt
whose conversation is still being created — against it.

At the cap:

- The composer disables Send and says why. A conversation whose own turn is the one running shows
  Stop instead, never the note.
- **Retry** is refused with a timed warning toast, one at a time.
- A prompt **handed over** from the project screen is not sent; it is kept in the composer.

Freeing a slot lifts all three at once. The cap is a client courtesy, not a server limit.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Conversations/ConversationService.cs` | The turn engine |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Conversations/ConversationLockService.cs` | One turn per conversation |
| `enterprise-gpt-ui/src/app/features/chat/chat-sessions.ts` | One session per conversation: its `TurnStore` and `UploadStore`, and when it is dropped |
| `enterprise-gpt-ui/src/app/features/chat/turn-store.ts` | Phase, send pipeline, settle discrimination, the cap on sends |
| `enterprise-gpt-ui/src/app/features/chat/chat-composer-host.ts` | The chat screen's `ComposerHost`: draft, project and export targets |
| `enterprise-gpt-ui/src/app/core/conversations/conversation-activity-store.ts` | Running and unseen endings, and the concurrent-turn cap |
| `enterprise-gpt-ui/src/app/features/chat/conversation-store.ts` | The conversation open on `/chat` |
| `enterprise-gpt-ui/src/app/core/composer/turn-settings-store.ts` | Model and MCP servers for the next turn |
| `enterprise-gpt-ui/src/app/core/composer/pending-prompt-store.ts` | The prompt handed between screens |
| `enterprise-gpt-ui/src/app/core/composer/pending-attachments-store.ts` | The files handed alongside it |
| `enterprise-gpt-ui/src/app/features/chat/turn-store.spec.ts` | The settle table, case by case |
| `enterprise-gpt-ui/src/app/features/chat/chat-sessions.spec.ts` | Session lookup, retention and disposal |

## Related

- [streaming.md](streaming.md)
- [transcripts.md](transcripts.md)
- [../frontend/answer-rendering.md](../frontend/answer-rendering.md)
