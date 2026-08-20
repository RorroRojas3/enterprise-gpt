# Conversation Download

How the rebuilt Angular client at `enterprise-gpt-ui/` turns a conversation into a file the reader can save: one menu behind two triggers, a store that treats the export response as bytes rather than a link, an object URL released on a delay instead of immediately, and an outcome the store hands out only once per instance even though two instances can be mounted at the same time.

Audience: a developer wiring the download control into a third surface, extending the store to a fourth format, or debugging a download that silently does nothing. Read [Conversation Export](../conversations/conversation-export.md) first for the API side — the renderer registry, the block model, and why PDF is the one format a deployment can genuinely not have — and [File Attachments §6](file-attachments.md#6-download-us-804) for `DocumentDownloadStore`, this feature's closest precedent and the store this one is shaped after, with one transport decision reversed (§1).

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md#us-1502-download-a-conversation), the authority for US-1502's acceptance criteria and frame `2f`.

## 1. Overview

One story, one control: **US-1502** puts a Download button in the composer's control row and in the conversation header, both opening the same menu — Markdown (`.md`), Word (`.docx`), PDF — behind whichever trigger the reader used.

Seven decisions shape everything here, and each looks removable until you know what it prevents:

1. **The response is bytes, not a signed URL.** The PRD allowed either. `DocumentDownloadStore` is a signed URL the browser follows, because a document already sits in blob storage. A conversation export does not: rendering one to blob storage first would make every export — markdown included — depend on storage being configured, and would leave a rendered transcript behind for something to clean up. So this store does the opposite of its closest precedent on purpose: it requests `responseType: 'blob'` and gets bytes back directly (§3).
2. **The request opts out of the retry interceptor, and the store reads its own error body back by hand.** `HttpClient` parses an error body as JSON only when the request's `responseType` is `'json'`; on a `blob` request it hands the body back as an unparsed `Blob`. Left alone, every application-typed problem on this request — including the 503 that means "this deployment cannot render that format" — degrades to the generic `http` arm, indistinguishable from a 503 that means "try again." `skipRetry()` stops the interceptor retrying blind, and `toAppErrorFromBlobError` reads the `Blob` back into the same typed `AppError` every other request gets for free (§5).
3. **The object URL is revoked after ten seconds, never at zero.** Revoking it synchronously, right after the anchor's `click()`, cancels the download the browser was just handed — the click is synchronous but the browser reads the URL afterwards. Waiting until the next microtask or macrotask is not enough either: some engines have not started reading by then, which produces a download that silently does nothing and reproduces on nobody's development machine. The blob is referenced by nothing else in the app, so the only cost of waiting is a short delay before the memory is released (§4).
4. **Two menus, one store, and an outcome that is a replaced record with a sequence number — not an `@ngrx/signals/events` event.** The composer and the conversation header both mount `ConversationDownloadMenu` at desktop widths (frame `2f`), and whichever instance's export is running has to close *its own* panel on success and leave the other alone. A one-shot the reader nulled back out would be invisible to whichever instance did not clear it; an event would work, but it is a heavier tool than "each of two listeners reacts to a fact exactly once" needs. `ExportOutcome.seq` increments on every settle, and each menu instance remembers the last sequence number it acted on (§6).
5. **The "Preparing `<format>`…" window is silent, and that is deliberate.** No live region announces it. The window is bounded on both ends — a failure raises an announced error toast, a success closes the panel and returns focus to the trigger — so nothing is ever silently stuck. Adding a live region here was considered and rejected in review: it would have to be reconciled with US-1402's turn-status region, which already owns the app's one narrated-progress convention, for a window that is at most a few seconds long (§9).
6. **The control is absent until there is something to export, and disabled — not absent — once there is but a turn is streaming.** `TurnStore.exportTarget()` is `null` on an empty conversation (there is nothing to render), which renders the control absent entirely, this repository's pattern for an affordance with nothing behind it. Once a conversation holds a persisted message, the control renders and is *disabled with a reason* while a turn is in flight, because there will be something to export the moment the turn finishes (§10).
7. **Two small changes rode along in the shared kit.** `Menu` gained an optional `hint` — a tooltip on the trigger, for a disabled menu that owes the reader a reason ("Available when the response finishes") — and now returns focus to its trigger when a consumer clears the two-way `open` model directly, the one close path that never went through `Menu`'s own `close()`. `Tooltip` now does nothing at all for an empty tip, rather than flashing an empty box and naming an unnamed host with the empty string (§9.2).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| The export formats, their labels, and the wire tokens | [`domain/api/conversation-export.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation-export.ts) — framework-free |
| The store: request, settle, save, revoke | [`core/conversations/conversation-export-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-export-store.ts) |
| The detached-anchor download primitive, extracted from `DocumentDownloadStore` so both stores share one implementation | [`core/downloads/anchor-download.ts`](../../enterprise-gpt-ui/src/app/core/downloads/anchor-download.ts) |
| Reading a blob-transport error back into a typed `AppError` | [`core/errors/to-app-error-from-blob.ts`](../../enterprise-gpt-ui/src/app/core/errors/to-app-error-from-blob.ts) |
| The retry opt-out context token | [`core/http/interceptors/retry.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/http/interceptors/retry.interceptor.ts) — `skipRetry` |
| The menu component and its two triggers' shared markup | [`shared/conversations/conversation-download-menu/conversation-download-menu.ts`](../../enterprise-gpt-ui/src/app/shared/conversations/conversation-download-menu/conversation-download-menu.ts), [`.html`](../../enterprise-gpt-ui/src/app/shared/conversations/conversation-download-menu/conversation-download-menu.html) |
| What the control acts on, and why it can be null | [`core/chat/composer-host.ts`](../../enterprise-gpt-ui/src/app/core/chat/composer-host.ts) — `ComposerExportTarget` |
| `exportTarget` and `inFlight`, the two signals the control reads | [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) |
| The two mount points | [`shared/composer/composer.html`](../../enterprise-gpt-ui/src/app/shared/composer/composer.html), [`features/chat/chat.html`](../../enterprise-gpt-ui/src/app/features/chat/chat.html) |
| `Menu`'s `hint` tooltip and its external-close focus return | [`shared/overlay/menu/menu.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/menu/menu.ts) |
| `Tooltip`'s empty-string no-op | [`shared/overlay/tooltip/tooltip.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/tooltip/tooltip.ts) |

## 2. Quick start

`ConversationExportStore` is `providedIn: 'root'` — inject it and call `download` from a click handler, never from a render:

```ts
import { inject } from '@angular/core';
import { ConversationExportStore } from '@core/conversations/conversation-export-store';

export class SomeDownloadTrigger {
  private readonly exports = inject(ConversationExportStore);

  onDownload(conversationId: string): void {
    this.exports.download(conversationId, 'docx');
  }
}
```

`download` is a no-op while another export is already `pending()` — the store takes one export at a time, and a second call while one is running is treated as a double-click on the same request rather than a second one.

## 3. The store: request lifecycle and the blob transport

`ConversationExportStore` composes `withState`, `withProps` and `withMethods` — not `withEntities`; there is exactly one export in flight at a time and nothing to key by id — plus `withResetOnSignOut()` last, as every store in this codebase does. Its `_download` `rxMethod<ExportRequest>` uses `exhaustMap`, not `switchMap` or `mergeMap`: a second press while one export is out is a double-click on the *same* file, not a request for a different one, so the second press is simply dropped rather than racing or queuing behind the first.

The request itself:

```ts
store._http.get(url, {
  params: new HttpParams().set('format', request.format),
  responseType: 'blob',
  observe: 'response',
  context: skipRetry(),
});
```

`observe: 'response'` is what makes the response headers reachable for the file name (§8); `responseType: 'blob'` is the transport decision in §1's first point. On success, `save()` hands the blob to a detached `<a download>` through `saveWithAnchor` (the same primitive `DocumentDownloadStore` uses) and the store patches an `ExportOutcome` with `ok: true`; on failure it patches one with `ok: false` and raises a toast (§5). Either way `pending` returns to `null` — there is no third "settled but still remembered" state for the request itself, only for the outcome record two menu instances read (§6).

The blob is never written into store state. It exists only for the duration of `save()`, as a local `URL.createObjectURL(blob)` string, and is gone once the timer in §4 fires — a transcript is the most sensitive thing this application holds, and a URL left alive in a signal would keep the whole rendered document in memory for the life of the page.

## 4. The object URL: revoked, deliberately not at zero

```ts
const url = URL.createObjectURL(blob);
try {
  saveWithAnchor(store._document, url, fileNameOf(response, request));
} finally {
  setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS); // 10_000
}
```

`anchor.click()` inside `saveWithAnchor` is synchronous, but the browser's own handling of the resulting download is not — it reads the URL afterwards, on its own schedule. Revoking at the same tick cancels a download that had not started reading yet; revoking on the very next task is not reliably enough later either, since some engines have not begun by then. The result of either is a download that does nothing, with no error surfaced anywhere, which reproduces on some machines and not others and is the kind of bug that costs an afternoon to reproduce once reported. Ten seconds is comfortably past every observed case and costs nothing to wait for, because nothing else references the blob.

## 5. Error handling

Three pieces work together, and each exists because of what the others cannot do on their own:

- **`skipRetry()`** builds an `HttpContext` carrying a token the retry interceptor checks before it retries any `GET`. Without it, an application-typed 503 — "this deployment has no PDF renderer" — would be retried three times with jittered backoff as if it were a transient gateway failure, because the interceptor cannot classify a `Blob` error body and treats it the same as any other unclassified 5xx.
- **`toAppErrorFromBlobError`** is what recovers the typed arm the interceptor could not classify. `HttpClient` parses an error body only when `responseType: 'json'`; on this request the failed response's `error` property is an unparsed `Blob`. The function checks for exactly that shape, reads the blob's text, and feeds it through the same `toAppErrorFromResponse` path the raw-`fetch` streaming code uses — so `export-renderer-not-configured`, `resource-not-found` and every other application problem type read identically whether the failing request went through `HttpClient` or `fetch`.
- **The resulting `AppError`** is fed to `ToastStore.fromError`, which raises an error toast naming the `traceId` — US-1502's fourth acceptance criterion. `retry-policy.ts`'s `AUTH_DECISIONS` table maps `export-renderer-not-configured` to `'passthrough'`: a 503 with an application type never triggers the app's 401-only token-refresh path, matching every other deterministic-deployment-state 503 in the app.

The menu **does not close on failure** — only on a successful settle (§6) — so the toast and the still-open panel are on screen together, and the reader can immediately try a different format without reopening anything.

## 6. Two triggers, one store: the sequence-numbered outcome

`ConversationDownloadMenu` is mounted twice at desktop widths — once in the composer, once in the conversation header — both reading the same root-provided `ConversationExportStore`. Each instance tracks `lastHandledOutcome`, a plain number, and an `effect()` compares it against `store.outcome()?.seq` on every change:

```ts
effect(() => {
  const outcome = this.exports.outcome();
  if (outcome === null || outcome.seq === this.lastHandledOutcome) {
    return;
  }
  this.lastHandledOutcome = outcome.seq;

  if (outcome.ok && outcome.conversationId === this.conversationId()) {
    this.open.set(false);
  }
});
```

Two guards matter here. **`seq` is what lets each instance react exactly once** to a settle that both instances observe — the outcome record is *replaced*, not appended to a queue, so without a per-instance high-water mark the second instance to run its effect would see an outcome it had already handled and close a panel that was never open. **`outcome.conversationId === this.conversationId()`** is what keeps one instance from acting on the other's result at all — relevant once routes change mid-export, though the store's own `pending` state already prevents two conversations exporting at once today.

`exportInFlight` — `this.exports.pending() !== null` — is deliberately **not** scoped to this instance's conversation: the store allows only one export across the whole app, so if a PDF for a different conversation is still rendering, this menu's three items dim too rather than looking live while the store would silently drop a second request.

## 7. The menu: busy, dimmed, and the footer note

`ConversationDownloadMenu` renders three `MenuItem`s from `EXPORT_FORMATS`, each carrying its own file-type glyph (`bi-filetype-md`, `bi-file-earmark-word`, `bi-file-earmark-pdf` — all already in the sprite). While one is preparing:

- The chosen item shows a spinning ring (the shared `spin` keyframe, so `_motion.scss`'s reduced-motion block covers it automatically) and the label changes to "Preparing `<format>`…".
- The other two dim, via `[class.download-menu__item--dimmed]`, and carry `aria-disabled="true"` — never the native `disabled` attribute, because a natively disabled item cannot take focus, which would strand the panel's own roving-focus and Escape handling. `onChoose` re-checks `exportInFlight()` regardless, so a click on a dimmed item is a guarded no-op rather than a hole the attribute alone would leave.
- `[stayOpen]="true"` on the underlying `Menu` keeps the panel on screen through the click that starts the download — a menu that closed on activation, as `Menu` normally does, would take the busy state and the dimmed items off screen with it, which is the opposite of frame `2f`.
- A footer note — "The stopped, unsaved answer on screen won't be included." — sits below the three items, tied to every item through `aria-describedby` rather than being a fourth `menuitem`, because a screen reader in menu mode visits only items, and this is a caveat rather than an action.

The trigger itself carries `[hint]` — `Menu`'s new tooltip input (§1, §9.2) — set to a reason only while `disabled()` is true, and the same sentence is folded into the trigger's accessible name via `triggerLabel()` so the reason reaches assistive technology even though the tooltip itself is visual only.

## 8. The file name

`fileNameOf` prefers the server's own choice, read from `Content-Disposition`, over anything built client-side:

1. RFC 5987's `filename*=UTF-8''…` first, `decodeURIComponent`-ed — this is what survives a conversation named using a script the plain `filename` parameter cannot carry.
2. The plain `filename="…"` parameter, if the encoded one is absent or fails to decode.
3. `conversation-{id}.{format}`, built here, only if the header did not survive the request at all.

The header surviving depends on the API's CORS policy exposing `Content-Disposition` to the browser client — which it already did, for document downloads, before this feature existed.

## 9. Accessibility

### 9.1 The silent "Preparing…" window

No live region announces the busy state itself. This was a deliberate choice, checked against — and agreed with — the accessibility reviewer during review, not an oversight the review missed: the window is bounded on both ends (§1, point 5) — a failure is always announced, through the error toast's live region, and a success always closes the panel and returns focus to the trigger, which a screen reader reports as the menu closing. What is silent is only the interval in between, which is at most a few seconds. Retire this only if the window is ever found to matter in practice; a live region here would need reconciling with US-1402's turn-status region, the app's one existing narrated-progress convention, and that reconciliation was judged not worth doing for a window this short.

### 9.2 `Menu.hint` and the external-close focus fix

Two changes to the shared `Menu` landed with this feature, both because a disabled menu that gives a reason and a menu that can close itself from the outside were new requirements no earlier consumer had:

- **`hint`** is applied through `[appTooltip]` on the trigger, not on `<app-menu>` itself: the directive would set `aria-label` on whatever host it decorates when that host has no name of its own, and `<app-menu>` carries no ARIA role, which axe reports as `aria-prohibited-attr` — a serious violation that would fail the accessibility gate. The trigger is a `<button>` that already has a name from `label`, so the tooltip stays purely visual there and the reason is carried to assistive technology through `label` instead (§7).
- **Focus return on an external close.** `ConversationDownloadMenu` clears `Menu`'s two-way `open` model directly on a successful settle (§6), which is a close path `Menu` had never had to handle before — every earlier consumer closed through `Menu`'s own `close()`, which already knew where focus should go. `Menu` now tracks whether it was open on the previous render pass and, if it was and the active element has fallen through to `<body>` with nothing to catch it, returns focus to the trigger itself — but only when the fall-through was not already the result of a deliberate `close(false)` (an outside press, which intentionally does not steal focus). Without this, a successful download would leave keyboard focus on `<body>` with the panel gone from under it.

### 9.3 `Tooltip`'s empty-string no-op

`Menu` binds `[appTooltip]="hint() ?? ''"` on every trigger unconditionally, and `hint` is `null` on every menu in the app except a disabled download menu — which means an empty string reaches `Tooltip` on nearly every hover of nearly every menu trigger in the product. `Tooltip` now short-circuits on an empty string in both the write phase (so it never sets `aria-label=""` on an unnamed host) and in `show()` (so it never writes the `visible` signal or registers a document-level dismiss listener at all) — the second one is the change that matters for cost, in a zoneless app where writing a signal is a change-detection pass, for a flyout that could never have rendered anything.

## 10. Absent vs. disabled

`ComposerExportTarget` (`{ id, name } | null`) and `TurnStore.exportTarget` are what decide which of the two states applies, and the two are read the same way from both mount points (§1.1):

- **Absent** — `@if (turn.exportTarget(); as target)` — when there is nothing to export at all: an empty `/chat`, a conversation whose transcript has not received its first persisted message yet, and every project screen, because `ProjectComposerHost` owns no transcript and reports `null` unconditionally. This is the repository's standing pattern for an affordance with nothing behind it, the same one the paperclip and the microphone use before their own enablers land.
- **Disabled** — `[disabled]="turn.inFlight()"` — once a transcript holds something, but a turn is currently streaming. There genuinely is something to export here; it is only not yet safe to, because the export would race the in-flight answer. The trigger stays reachable and names the reason (§7, §9.2) rather than disappearing, because the wait is temporary and the reader may want to know why.

`exportTarget` deliberately tests `entries().length === 0`, not `hasContent()`: `hasContent` is true while history is merely *pending* or while a pre-stream notice is on screen, neither of which the transcript store has actually persisted yet, and offering a download of a conversation with nothing durable behind it would be worse than the brief absence.

## 11. Bundle cost

The initial bundle grows **0.91 kB**, from the 670.68 kB baseline to **671.59 kB raw / 168.92 kB transfer**, against the unchanged 675 kB warn / 720 kB error budget — about 3.4 kB of headroom remains. The growth is on the initial graph for a specific reason: the new `core/errors/` arms (`ExportRendererNotConfiguredAppError` and its entries across `app-error.ts`, `problem-types.ts`, `retry-policy.ts`) and `EXPORT_FORMAT_LABELS` are reached from code that is already initial, since error normalization is app-wide. `ConversationDownloadMenu` and `ConversationExportStore` themselves are not new weight on the initial graph — they ride the shared, already-lazy composer chunk that the chat route pulls in. This story does not own the bundle budget and does not re-baseline it.

## 12. Testing

**1830 tests pass** (up from 1788) and **38 browser accessibility audits pass** (up from 36 — the two new ones audit the download menu **open**, in both themes, because that is where its ARIA semantics actually live: a `role="menu"` whose children must all be `menuitem`, and the footer note deliberately is not one). `npm run lint`, `npm run format:check`, `npm run build` and `npm run check:contract` are all green.

`conversation-download-menu.spec.ts` covers the busy/dimmed states, the stays-open-until-success behaviour, cross-conversation dimming, the disabled-with-reason trigger, and ignoring an outcome that belongs to a different conversation — the case §6 exists to prevent.

## 13. Deliberately not here

- **`html` and `json` are not offered.** Both remain on the API route unchanged ([Conversation Export §10](../conversations/conversation-export.md#10-known-limits)); this client's menu draws exactly the three formats frame `2f` draws.
- **No caching, no prefetch.** Every download is a fresh request; there is no reason to warm one before the reader asks for it, and the response is never kept around once handed to the browser (§3).
- **No signed URL, ever, for this feature.** Unlike document downloads, there is no click-then-fetch-a-link step to add later — the design in §1 is not a stopgap.

## 14. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| The Download control does not render at all | `TurnStore.exportTarget()` is `null` — either no conversation is bound, or its transcript has not received its first persisted message. Check the mount's host implements `ComposerHost.exportTarget` (§10) |
| The control renders but is disabled with "Available when the response finishes" | A turn is streaming (`turn.inFlight()` is `true`). This is expected; the control re-enables once the turn settles |
| An item stays "Preparing…" indefinitely with no toast | Check the network tab for the export request — a request that never resolves (dropped connection, or a very large PDF) will not settle either signal. Nothing here has a client-side timeout |
| A format's item is present but every attempt raises a toast naming a `traceId` | Read `problem+json`'s `type` from the network response. `export-renderer-not-configured` means the deployment has withdrawn or cannot build that renderer — see [Conversation Export §3–§4](../conversations/conversation-export.md#3-the-renderer-registry) |
| The download silently does nothing — no file appears, no error | Almost certainly the object-URL revoke race §4 describes, if this code was changed. Confirm `REVOKE_DELAY_MS` is still applied via `setTimeout`, not synchronously |

## 15. Key files

| Concern | File |
| --- | --- |
| Store | [`core/conversations/conversation-export-store.ts`](../../enterprise-gpt-ui/src/app/core/conversations/conversation-export-store.ts) |
| Wire shapes and labels | [`domain/api/conversation-export.ts`](../../enterprise-gpt-ui/src/app/domain/api/conversation-export.ts) |
| Menu component | [`shared/conversations/conversation-download-menu/conversation-download-menu.ts`](../../enterprise-gpt-ui/src/app/shared/conversations/conversation-download-menu/conversation-download-menu.ts) |
| Blob-error normalization | [`core/errors/to-app-error-from-blob.ts`](../../enterprise-gpt-ui/src/app/core/errors/to-app-error-from-blob.ts) |
| Retry opt-out | [`core/http/interceptors/retry.interceptor.ts`](../../enterprise-gpt-ui/src/app/core/http/interceptors/retry.interceptor.ts) |
| Anchor-download primitive | [`core/downloads/anchor-download.ts`](../../enterprise-gpt-ui/src/app/core/downloads/anchor-download.ts) |
| Export target signals | [`core/chat/composer-host.ts`](../../enterprise-gpt-ui/src/app/core/chat/composer-host.ts), [`features/chat/turn-store.ts`](../../enterprise-gpt-ui/src/app/features/chat/turn-store.ts) |
| Mount points | [`shared/composer/composer.html`](../../enterprise-gpt-ui/src/app/shared/composer/composer.html), [`features/chat/chat.html`](../../enterprise-gpt-ui/src/app/features/chat/chat.html) |
| Shared-kit changes | [`shared/overlay/menu/menu.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/menu/menu.ts), [`shared/overlay/tooltip/tooltip.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/tooltip/tooltip.ts) |
| Accessibility audits | [`features/chat/chat.a11y.spec.ts`](../../enterprise-gpt-ui/src/app/features/chat/chat.a11y.spec.ts) |
| Related reference | [Conversation Export](../conversations/conversation-export.md) (the API), [File Attachments](file-attachments.md) (the closest precedent store), [Design System](design-system.md) (`Menu`, `Tooltip`), [the rebuild PRD, US-1502](../prd/enterprise-ui-rebuild.md#us-1502-download-a-conversation) |
