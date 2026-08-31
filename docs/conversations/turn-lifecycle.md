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

`inFlight` is anything but `idle`, and is what morphs the composer's button into Stop.
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

## Sending

The send pipeline is one `rxMethod` on `exhaustMap`, which is the double-send guard. A send while a
turn is in flight is **ignored, not queued** — a queued one would only meet the server's 409,
because a conversation runs one turn at a time. `send(prompt)` re-checks what the composer's
disabled button already enforces, so no caller can issue a turn without a `modelId`.

### The first prompt creates the conversation

"New conversation" creates nothing; no conversation exists until the first prompt is sent. When a
send finds no bound conversation the store:

1. Enters `creating` and posts `api/conversations`.
2. On the 201, prepends the new conversation into `ConversationListStore` so the sidebar row exists
   the moment the URL updates, patches `boundConversationId`, **then** navigates.
3. Navigates with `router.navigateByUrl('/chat/{id}', { replaceUrl: true })` and streams against the
   new id.

The ordering in step 2 is load-bearing. `bindRoute` follows the route's `conversationId` input and
resets everything when the conversation changes, but treats an incoming id equal to
`boundConversationId` as "the URL caught up with our own create" and passes it through as a no-op.
Setting the id *before* navigating is what makes the store's own navigation indistinguishable from
no change.

`replaceUrl: true` rather than `Location.replaceState`, because the latter moves the URL without
moving the Router: `routerLinkActive` selection breaks, "New conversation" becomes a same-URL no-op,
and the route's `conversationId` input never binds.

### When the create goes wrong

- **The POST fails** — the prompt goes straight back into the composer and a failure notice renders
  *without* a Retry button, because the reseeded composer is the retry surface.
- **Stop or navigation mid-create** — the POST is cancelled and the prompt reseeded. A route event
  during `creating` is always foreign, since the store's own navigation cannot fire before the 201
  has moved the phase on, so `bindRoute` cancels the create before resetting. Without that the 201
  would land after the reset and navigate the user off the conversation they just opened.
- **Sign-out mid-create** — the same `takeUntil` races `injectSignedOut()`.

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

- **`aborted` is discriminated by phase, never by abort reason.** Stop, sign-out and unsubscribe all
  surface as the same `aborted` arm. What distinguishes them is what the store did first: Stop flips
  the phase to `stopping` before aborting, while sign-out and route changes reset to `idle` before
  the rejection lands.
- **`Finished` racing a Stop wins.** The server transcribed the answer, so a card claiming "not
  saved" would be false.
- **The settle is one atomic patch.** The optimistic user bubble is a *field* (`pendingUserText`),
  not a transcript entry, so `Finished` appends both entries in a single `patchState` and Stop
  removes the bubble by nulling a field rather than popping an array.

A body that ends without `Finished` is a deliberate **non-error** arm: a mid-stream fault and a
network drop are indistinguishable on the wire, so the partial answer keeps its place under a "This
response was cut off" warning, with a Retry that re-sends the turn's own prompt and selection as
sent, even if the pickers have moved since.

## Stop

- **During `creating`** — the POST is cancelled and the prompt returns to the composer. Nothing
  streamed, so there is no partial output to card.
- **During `awaitingFirst` or `streaming`** — the phase flips to `stopping` *before* the abort, both
  for the settle discrimination above and because the synchronous patch returns the Send control
  immediately rather than after the rejection's round trip.

The result is a **detached stopped card**: dashed border and left inset, stating that this text is
not part of the transcript. The server transcribed neither the answer nor the prompt, and the card
vanishes on route change, reopen and reload because the state behind it does. It offers Copy, and a
Retry that restores the prompt *and* the turn's model and MCP selection into the pickers without
re-sending.

There is no stop endpoint and none is called: aborting the fetch is what ends the turn server-side.
The server still records what the stopped turn spent.

## Reopening

Replay hangs off the reset, not the route input, and a replayed answer goes through the same
reducers as a live one — so there is one rendering path, not two.

Points worth knowing:

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

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs` | The turn engine |
| `enterprise-gpt-api/Enterprise.Gpt.Service/ConversationLockService.cs` | One turn per conversation |
| `enterprise-gpt-ui/src/app/features/chat/turn-store.ts` | Phase, send pipeline, settle discrimination |
| `enterprise-gpt-ui/src/app/features/chat/conversation-store.ts` | The conversation open on `/chat` |
| `enterprise-gpt-ui/src/app/core/chat/turn-settings-store.ts` | Model and MCP servers for the next turn |
| `enterprise-gpt-ui/src/app/core/chat/pending-prompt-store.ts` | The prompt handed between screens |
| `enterprise-gpt-ui/src/app/features/chat/turn-store.spec.ts` | The settle table, case by case |

## Related

- [streaming.md](streaming.md)
- [transcripts.md](transcripts.md)
- [../frontend/answer-rendering.md](../frontend/answer-rendering.md)
