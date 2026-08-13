# Answer Rendering

How the rebuilt Angular client at `enterprise-gpt-ui/` turns model output into readable, inert HTML: two independent layers that between them guarantee no raw markup reaches the DOM, a head/tail split that keeps a long answer smooth while it streams, and the scroll pinning that decides whether the page follows the newest text or leaves the reader where they are.

Audience: a developer building the rest of EP-6 (US-603 copy, US-604's theme swap, US-605's deferred diagram and math renderers, US-607), reviewing the client's XSS posture, or debugging a transcript that renders, scrolls or highlights strangely. Read [Conversation Turn Lifecycle](../conversations/turn-lifecycle.md) first for `TurnStore`, the snapshot/timeline join these renderers consume, and [Frontend Foundation](frontend-foundation.md) for the build gates §6 leans on.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Three P0 stories landed together and close phase P3 — the minimum viable chat replacement:

| Story | What it delivers |
| --- | --- |
| **US-601** | Assistant text renders as markdown that cannot execute: raw HTML dropped at the parser, DOMPurify with a closed profile at the DOM boundary, and the whole renderer behind the lazy chat route |
| **US-602** | A streaming answer renders as a **stable head and a volatile tail** — two `<markdown>` instances — so a long turn re-parses one block instead of the whole answer per flush |
| **US-606** | The transcript follows the newest content while the reader is at the bottom, holds position when they are not, and offers frame `1b`'s 44 px jump-to-latest control back |

Six decisions shape everything here, and each looks removable until you know what it prevents:

1. **Two independent layers, not one.** marked has no `html: false`, so a `MarkedRenderer` override drops raw HTML tokens at the parser *and* DOMPurify filters the result. Either one alone would be the whole boundary; together, a regression in one is not an exploit (§3).
2. **Application code performs no trust operation.** `ngx-markdown` calls `bypassSecurityTrustHtml` internally, so `src/` holds **zero** call sites and US-108's lint rule fails a build that introduces one (§3.3).
3. **Images are dropped, and that is policy.** Not a rendering gap: FR-51 says the client issues no third-party request at run time, and a remote image in a model answer — or in a tool result the model is repeating — is both such a request and a read receipt for whoever hosts it. The alt text renders in its place (§3.4).
4. **The streaming split is framework-free and its head only ever grows.** `splitStreamingMarkdown` lives in `domain/` and tests in Node. A boundary that could move backwards would re-parse the whole answer on the flush that moved it (§4).
5. **Pinning is driven by a `ResizeObserver`, not by the turn's signals.** The markdown renderer writes its output a microtask after change detection returns, so a scroll taken during the render phase measures the height the page had *before* the text it is meant to follow (§5.2).
6. **The renderer rides the lazy chat chunk.** `provideChatMarkdown()` is provided at the `Chat` component, and `check-initial-chunk.mjs` fails the build if any of the stack becomes statically reachable from `main.ts`. This is the resolution of the PRD's last open question (§6).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| The renderer, the DOMPurify profile, the marked overrides | [`features/chat/markdown/markdown-providers.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/markdown-providers.ts) |
| Prism and the seven grammars the transcript highlights | [`features/chat/markdown/prism.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/prism.ts) |
| The head/tail split and `stripFenceInfo` | [`domain/markdown/streaming-split.ts`](../../enterprise-gpt-ui/src/app/domain/markdown/streaming-split.ts) — framework-free |
| The two-renderer template and the caret | [`features/chat/transcript/assistant-turn.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.html) |
| Pinning, bottom detection, the jump control's state | [`features/chat/transcript-pinning.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts) |
| The scroll container, the sentinel, the jump button | [`features/chat/chat.html`](../../enterprise-gpt-ui/src/app/features/chat/chat.html), [`chat.scss`](../../enterprise-gpt-ui/src/app/features/chat/chat.scss) |
| Styling for rendered output (global — it is `innerHTML`) | [`src/styles/_markdown.scss`](../../enterprise-gpt-ui/src/styles/_markdown.scss), [`_prism.scss`](../../enterprise-gpt-ui/src/styles/_prism.scss) |
| The build gate that keeps the stack lazy | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs) |
| Controllable observers for tests | [`src/testing/intersection-observer.ts`](../../enterprise-gpt-ui/src/testing/intersection-observer.ts), [`resize-observer.ts`](../../enterprise-gpt-ui/src/testing/resize-observer.ts) |

## 2. Quick start

### 2.1 Rendering markdown on a chat surface

The providers are already installed on the chat route, so a component under it only binds the source text:

```html
<markdown class="assistant-turn__md" [data]="node.text" />
```

`MarkdownComponent` is a standalone import from `ngx-markdown`. The `assistant-turn__md` class is what the global stylesheet targets (§7) — without it the output renders unstyled.

### 2.2 Rendering markdown on a surface that is not chat

Provide the same factory on that route's component, never in `app.config.ts`:

```ts
@Component({
  // ...
  providers: [ConversationStore, TurnStore, provideChatMarkdown()],
})
export class Chat {}
```

Three rules that the build enforces rather than merely recommends:

- **Never import `ngx-markdown`, `marked`, `prismjs` or `dompurify` from code reachable by a static import from `main.ts`.** `npm run build` fails naming the library and the chunk (§6).
- **Never call `bypassSecurityTrustHtml`.** `npm run lint` fails on the syntax, and the renderer already performs the trust behind its own sanitizer.
- **Never widen the profile in place.** `sanitizeChatMarkdown` passes its configuration per call rather than through `DOMPurify.setConfig`, so no other caller can inherit or widen it. A surface that genuinely needs different allowances gets its own function.

## 3. Two layers between model output and the DOM

Model output is untrusted input. It can carry text from an uploaded document, from an MCP tool result the model is quoting, or from a prompt another user wrote into a shared document — so "the model would not emit that" is not a control. Two layers stand between it and the DOM, and they fail independently.

### 3.1 Layer one — raw HTML never becomes markup

marked offers no `html: false` switch. `html` is the one renderer it routes both block-level and inline raw-HTML tokens through, so returning an empty string from it suppresses raw markup at the parser:

```ts
const renderer = new MarkedRenderer();
renderer.html = () => '';
```

`<script>alert(1)</script>` never becomes markup at all; an inline `<em>x</em>` degrades to its text. The override must be an **own** property of the renderer instance: `ngx-markdown` hands marked a spread of it, which copies own properties and leaves the prototype's defaults in place for every token kind not named.

### 3.2 Layer two — DOMPurify with a closed profile

Whatever survives parsing is filtered before it is written:

```ts
DOMPurify.sanitize(html, {
  ALLOWED_TAGS: [...CHAT_ALLOWED_TAGS],
  ALLOWED_ATTR: [...CHAT_ALLOWED_ATTR],
  ALLOW_DATA_ATTR: false,
});
```

`CHAT_ALLOWED_TAGS` is a **closed set** — every entry is something marked emits from markdown syntax, and a tag that is not on it cannot reach the DOM however it was spelled in the source. `CHAT_ALLOWED_ATTR` is traceable the same way: `href`/`title` to links, `class` to the `language-*` marker on a fenced block, `type`/`checked`/`disabled` to GFM task lists, `start` to an ordered list that does not begin at one, `align` to a table column.

Two consequences worth knowing:

- **`span` is not allowed, and Prism still works.** Highlighting runs on the DOM *after* sanitizing, so Prism's own `span.token` markup needs no allowance. Anything that moves highlighting before the sanitizer breaks it.
- **Allowing `href` is not allowing any href.** DOMPurify's default URI test still rejects `javascript:` and friends, dropping the attribute and leaving the link text inert.

### 3.3 Angular's own sanitizer is not in this path

Supplying a function through `ngx-markdown`'s `SANITIZE` provider **replaces** its use of Angular's `DomSanitizer` rather than adding to it. DOMPurify is therefore the only DOM-boundary layer, which is why its profile is closed rather than merely restrictive, and why the spec for it drives real payloads through the real pipeline instead of stubbing either half.

Nothing in the app holds a `SafeHtml`: stores hold markdown **source**, the renderer holds the HTML for the length of one write, and `bypassSecurityTrustHtml` appears nowhere in `src/`.

### 3.4 The renderer overrides, and what they are for

Beyond dropping HTML, three overrides and one option:

| Override | Why |
| --- | --- |
| `renderer.checkbox` gives each box an `aria-label` — "Completed" / "Not completed" | A task-list checkbox is a real, if disabled, form control, and a control with no label is a failure however inert it is. It is **named rather than hidden**, because the box is the only thing carrying done-ness: "done" and "todo" read identically, so hiding it would take the state away from a screen reader entirely |
| `renderer.heading` shifts every level down **one**, clamped at `h6` | The conversation title is the page's `h1`, so a model's `#` would open a second one. Shifting by one slots the answer's hierarchy directly beneath the title; shifting by two would jump `h1` → `h3`, itself a heading-order failure |
| `renderer.image` renders the alt text, escaped | Images are dropped by the profile so the client issues no third-party request (§1, decision 3). Rendering `text \|\| title` keeps the description the model wrote instead of leaving a hole — and it is escaped here, because alt text is raw source and would otherwise re-enter the pipeline as markup for the sanitizer to judge |
| `breaks: true` | Model output leans on single newlines for structure far more than prose markdown does, and the transcript rendered them literally before US-601. Collapsing them now would silently reflow answers users had already read |

The heading override is a `function`, not an arrow, and that is load-bearing: marked binds the live renderer as `this`, so `this.parser.parseInline(tokens)` uses the parser carrying **these** options. The static `Parser.parseInline` would build a fresh one with marked's defaults — quietly exempting everything inside a heading from layer one's raw-HTML suppression. A spec drives `## <script>…</script> heading` through the pipeline for exactly that reason.

`aria-label` is not in `CHAT_ALLOWED_ATTR` and survives anyway: DOMPurify allows ARIA attributes by default (`ALLOW_ARIA_ATTR`). Do not "fix" that by listing it — the list is for the attributes markdown syntax produces.

## 4. Streaming: a stable head and a volatile tail

`ngx-markdown` re-parses its **entire** `[data]` input on every change. Bound to an accumulating answer, that is a full re-parse per ~16 ms flush, and the cost grows with the answer — the exact shape US-602 exists to prevent. So the growing text node renders as two instances:

```html
<div class="assistant-turn__live">
  <markdown class="assistant-turn__md" [data]="liveHead()" />
  <markdown class="assistant-turn__md" [data]="liveTail()" />
</div>
```

The head is byte-identical between flushes until a new block closes, and an unchanged string is not a changed input — so **memoization here is plain string equality**, with no cache to invalidate. Only the tail re-parses per flush.

Only the node still growing is split. A text node closed by an intervening activity card never changes again, and `AssistantTurn` re-derives its identical slice each flush, so it too stops re-parsing by the same mechanism.

### 4.1 Where the split lands

`splitStreamingMarkdown(text)` returns `{ head, tail }` with `head + tail === text`, cut at the **last** point where one block ends and the next begins. Three things start a block, and the latest wins:

- a **blank line** — the ordinary case;
- the **opener of an unclosed fence** — a head cut off mid-fence would parse as an unterminated code block, and a tail starting mid-fence would not parse as code at all, so everything from the opener stays together;
- the **line after a closed fence**, which is what keeps the head from collapsing when a fence had no blank line before it.

Three refusals matter as much as the rules:

- A blank line **inside** a fence is content, not a boundary.
- A boundary whose next line is **indented** is refused: the two halves are parsed separately and marked re-bases the indentation of whatever it is given, so an indented tail would come out as a paragraph where the whole source gives an indented code block.
- A boundary at the **very end of the text** is refused on the same rule, because the line after it has not arrived. The verdict has to be one-way — refused until proven otherwise — or a boundary accepted while the next line was still empty would be withdrawn the moment a space arrived, and the head would shrink. A text ending in a blank line therefore keeps it in the tail for one more delta.

Fences are paired the way CommonMark pairs them — a block closes only on a later fence of the *same* character that is at least as long — so a four-backtick block can quote a three-backtick one without the inner pair terminating it. The delimiter stays with the head.

> **The head only ever grows.** Every rule above is written to preserve that, and a spec asserts it across every prefix of several fixtures. A boundary that could move backwards would hand the renderer a *different* head, re-parsing the whole answer on the flush that moved it — the failure mode the split exists to avoid, arriving intermittently.

Offsets are byte-for-byte, so the source must use `\n` line endings. The SSE codec guarantees that; `\r\n` would simply never match a boundary and the answer would render as one block.

### 4.2 The tail is not highlighted

`stripFenceInfo` removes the info string from every line-start fence in the tail, so a still-arriving code block has no `language-*` class and Prism leaves it alone. Mid-stream that is the honest rendering — the fence may be half a line of code, and highlighting it against a grammar it does not yet satisfy produces flickering nonsense. The head keeps its info strings and highlights normally.

### 4.3 Settling

When the turn settles, the caret index goes to `-1`, the pair collapses, and the whole node renders once from the **untouched** source — fences, languages, highlighting and all. A spec asserts that this settled render is character-identical to a single full render of the same source, which is what makes the split an optimisation rather than a second rendering mode.

### 4.4 The caret, and why the pair is wrapped

Frame `1b`'s 8×17 px `--accent` caret rides whichever renderer ends the text — the tail normally, the head on the rare flush where the split leaves nothing over — and is drawn as a CSS pseudo-element, because the output is `innerHTML` and there is no template position left inside it for a span.

The two renderers are wrapped in one `.assistant-turn__live` element so the pair counts as a single child of the content column. As two children the column's 10 px gap would space them apart, then close to a paragraph margin the moment the turn settled into a single render — a visible jump at the end of every answer.

## 5. Following the newest content (US-606)

[`TranscriptPinning`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts) is a directive on the scroll container itself, so the host element is both the thing scrolled and the observer's root.

### 5.1 Bottom detection

An `IntersectionObserver` watches a zero-height sentinel with `rootMargin: '0px 0px 80px 0px'` — the tolerance that treats a reader a line or two short of the end as still following. A scroll listener was rejected: it runs on the main thread for every frame of a flick and would still have to measure to answer the only question asked here.

The sentinel is **passed into the directive rather than queried**, and it lives outside the body's `@if` chain as an unconditional last child. One element, one observation, across the error / transcript / empty-state swaps — a sentinel inside a branch would be replaced on every swap and leave the observer watching a detached node.

`_atBottom` starts `true`. A real observer's first record is asynchronous, and treating "not yet reported" as "scrolled up" would flash the jump control on every mount.

### 5.2 Following is a `ResizeObserver`, not a signal effect

This is the decision most likely to be "simplified" back into a bug. The answer is not in the DOM when the turn's signals settle: the markdown renderer writes its output a microtask *after* change detection returns, so a scroll taken during the render phase measures the height the page had before the text it is meant to follow — the transcript trails the answer by one flush, visibly.

A `ResizeObserver` on the content wrapper fires when the geometry actually changes, whatever produced it: the answer, an activity card, a notice, or the renderer's own deferred write, which no Angular lifecycle hook is late enough to catch. It cannot loop, because moving `scrollTop` changes no element's size.

The wrapper exists for the same reason — the scroll container's own box never changes as its content grows, so watching it would report nothing.

### 5.3 The jump control

Frame `1b`'s 44 px circle, centred above the composer and anchored to it so it keeps its 14 px gap as the textarea grows:

- It shows whenever the reader is away from the bottom and there is a transcript to return to — **including after the turn ends**, because `Finished` deliberately does not scroll and hiding the control then would strand the reader at the point they stopped.
- It pulses (`ringpulse 1.8s ease-out infinite`) only while the turn is in flight. The ring is on a wrapper rather than the button because the animation drives `box-shadow`, which would otherwise replace the button's own drop shadow for most of every cycle.
- Pressing it scrolls to the bottom, sets `_atBottom` **in the handler** (so the control disappears on the same change detection as the scroll rather than a frame later), and hands focus to the scroll container, which carries `tabindex="-1"` for exactly this. Without that, focus would fall to `<body>` and a keyboard or screen-reader user would lose their place on the page. The container draws an **inset** focus ring on `:focus-visible` — the confirmation that the jump worked and that focus went somewhere deliberate; inset because an outline drawn around a full-height scroll region reads as a rendering fault.

### 5.4 Container rules

`.chat__body` sets `overflow-anchor: none` — the browser's scroll anchoring would otherwise fight the directive, holding position against content the reader asked to follow — and `overscroll-behavior: contain`, so a flick at either end does not scroll the page behind the transcript.

## 6. The bundle gate, resolved

The PRD's last open question and [the build order's ⛔ gate at US-601](../prd/enterprise-ui-rebuild-build-order.md) both came due at the *start* of this story: pay for `ngx-markdown` + marked + Prism + DOMPurify in the initial bundle and re-baseline, or move the transcript renderer behind the lazy chat route.

**It went behind the lazy route.** `provideChatMarkdown()` is provided on the `Chat` component, and `check-initial-chunk.mjs` gained a FORBIDDEN entry for `ngx-markdown|marked|prismjs|dompurify` beside the ones US-605 and US-203 already had, so a static import from eagerly reachable code fails the build naming the library and the chunk.

| | Initial raw | Initial transfer | `chat` chunk | `styles` | Budget (warn / error) |
| --- | --- | --- | --- | --- | --- |
| Before EP-6 (end of US-501) | 648.75 kB | 157.96 kB | 48.60 kB | 60.50 kB | 660 kB / 720 kB |
| **After US-601/602/606** | **660.24 kB** | **161.01 kB** | **179.47 kB** | **62.08 kB** | **665 kB / 720 kB** |

The whole **124.8 kB** markdown stack is in the lazy chat chunk and none of it is in the initial graph — marked 42.5 kB, Prism 40.8 kB, DOMPurify 29.0 kB, `ngx-markdown` 12.5 kB.

**The ~11.5 kB the initial graph did grow by is not the markdown libraries.** It is Angular's own `DomSanitizerImpl` — `ngx-markdown`'s `MarkdownService` injects `DomSanitizer`, which lands in a chunk shared between initial and lazy code — plus ~1.6 kB of global CSS for the rendered output. Unavoidable without dropping `ngx-markdown`, and the reason the **warning** line moved 660 → **665 kB**. The **error ceiling stays 720 kB**, which is the number that matters; the `styles` budget is unchanged at 65 / 80 kB.

Two smaller build facts came with it: `@types/prismjs` is a new devDependency, and `angular.json` carries `allowedCommonJsDependencies: ["prismjs"]`, because Prism ships CommonJS and the builder warns on every such import otherwise.

## 7. Styling output the templates never see

Rendered markdown is written as `innerHTML`, so it carries no `_ngcontent` attribute and **no component stylesheet can match it**. Its rules are global, in [`src/styles/_markdown.scss`](../../enterprise-gpt-ui/src/styles/_markdown.scss), scoped by the `.assistant-turn__md` class on the host — nothing there applies outside a rendered answer. The import sits after `prism` in the `styles.scss` chain, whose code-block rules it defers to.

Two rules in it are load-bearing:

- **Every block carries its spacing as a top margin only**, and no edge is reset. While a turn streams the growing node is two renderers, so an edge rule would space head and tail differently from the single render that replaces them at settle.
- **No `overflow` on `pre`.** `_prism.scss` sets `overflow: auto` at the same specificity and is imported first, so anything set here would win and clip every code line wider than the column — unreachable by mouse, keyboard or touch.

Highlighting itself is token-driven: no Prism stylesheet is imported, because `--code-bg` is dark in **both** themes and neither of Prism's shipped themes matches the design. A theme change re-tints code on the same paint as `data-bs-theme` flipping. Prism's grammar list is a deliberate floor — TypeScript, C#, Python, Bash, JSON, YAML, SQL, on top of the markup/CSS/clike/JavaScript core already carries — because each one is bytes in the chat chunk and an unlisted language renders as an unhighlighted block rather than an error.

## 8. Testing

The suite is **894 specs across 78 files**, green alongside `npm run lint` and `npm run build`.

| Area | Spec | Notable cases |
| --- | --- | --- |
| The sanitizing pipeline | `markdown-security` (14) | The three payloads the story names — `<img src=x onerror=…>`, `<script>`, a `javascript:` href — driven through the **real** renderer and profile rather than a stub, because a spec that mocked either layer would prove nothing; raw HTML dropped with the sanitizer switched off, proving layer one alone; data attributes dropped and the `language-*` class kept; images dropped; `span` refused; the renderer overrides, including **raw HTML inside a heading**, which regressed once when the override built its own parser |
| The split | `streaming-split` (30) | Last boundary not the first; the delimiter staying on the head; a trailing blank line held back until the line after it arrives; an unclosed fence overriding the blank-line rule; a blank line inside a fence ignored; tilde fences, and a backtick line refusing to close one; a shorter fence refusing to close a longer one; indented continuations refused; text opening on a blank line returning rather than hanging; **the head only ever growing, asserted across every prefix of several sources** |
| The two renderers | `assistant-turn` (8) | Two renderers while streaming and one once settled; closed blocks in the head and growing text in the tail; the caret on the tail, and on the head when nothing is left over; **only the tail re-parsing until a boundary is crossed**; a still-arriving code block unhighlighted and the same block highlighted at settle; **the settled render character-identical to a single full render** |
| Pinning | `transcript-pinning` (8) | The sentinel observed against the container with the 80 px margin; following while at the bottom; holding position and offering the control once scrolled up; the control scrolling, resuming and **handing focus to the transcript rather than dropping it**; no jump when a turn finishes while scrolled up; the same sentinel surviving an empty-state → transcript swap |

jsdom implements neither observer, and could not usefully: with no layout, nothing ever intersects or resizes. [`src/testing/intersection-observer.ts`](../../enterprise-gpt-ui/src/testing/intersection-observer.ts) and [`resize-observer.ts`](../../enterprise-gpt-ui/src/testing/resize-observer.ts) install controllable fakes from `setup-dom.ts`, and specs drive them with `setIntersecting` / `resizeElement`. The intersection fake deliberately **does not** fire on `observe`: a real observer's first record is asynchronous, so code that is only correct once it arrives is code with a visible wrong state first, and leaving the fake silent keeps that mistake failing.

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 894 specs
npm run lint    # ESLint (incl. the bypassSecurityTrustHtml ban) + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs — the markdown stack must stay lazy
```

> **Verification status.** As with the epics before it, none of this has been exercised against a live API: the gates above are green and every behaviour here is asserted in Vitest, with the stream driven from recorded fixtures.

## 9. Deliberately not here

Recorded in the PRD and the build order, not omissions:

- **The code-block chrome and its copy control** — US-603/604. `_markdown.scss` keeps a neutral `pre` margin so the `--code-head` bar can be layered on without moving anything, and `_prism.scss` already carries US-604's token-driven theming.
- **Diagrams and math** — US-605, which loads Mermaid or KaTeX by dynamic import behind a `config.json` flag. Both already have FORBIDDEN entries in the initial-chunk check.
- **Copy on a prompt or a response** — US-607.
- **The streaming live-region treatment** — US-1402. Today `aria-busy` rides the transcript for the length of a turn and a persistent `role="status"` region announces abnormal endings only ([turn lifecycle §8](../conversations/turn-lifecycle.md#8-rendering-the-turn)).

## 10. Troubleshooting

| Symptom | Cause |
| --- | --- |
| An image in an answer renders as its alt text | Deliberate (§1, decision 3, and §3.4). `img` is absent from `CHAT_ALLOWED_TAGS` so the client issues no third-party request; the alt text is the substitute, not a fallback that failed |
| Ordinary markup vanishes from an answer | It arrived as raw HTML, which layer one drops at the parser. Only markdown syntax renders — that is the boundary, not a bug |
| A code block is not highlighted mid-stream | Expected: `stripFenceInfo` removes the info string from the tail, and highlighting returns with the canonical render at settle (§4.2). If it is missing *after* settle, check the language is one of the seven grammars `prism.ts` registers |
| The whole answer re-renders on every flush | The head moved backwards. Something changed a boundary rule in `splitStreamingMarkdown`, or the source arrived with `\r\n` line endings, which match no boundary at all (§4.1) |
| The transcript lags one flush behind the answer | Pinning was moved onto a signal effect. The renderer writes a microtask after change detection returns; only the `ResizeObserver` sees the real geometry (§5.2) |
| The jump control flashes on every mount | `_atBottom` no longer starts `true`. A real observer's first record is asynchronous, and "not yet reported" is not "scrolled up" (§5.1) |
| The jump control never disappears, or the page jumps back | The sentinel was moved inside the body's `@if` chain, so a body swap replaced it and the observer is watching a detached node (§5.1) |
| Focus lands on `<body>` after pressing jump | The container lost its `tabindex="-1"`, so the directive's `focus()` is a no-op and focus falls through when the control removes itself (§5.3) |
| The build fails naming `ngx-markdown` or `prismjs` | Something eagerly reachable from `main.ts` now imports the renderer statically. Move the import behind the lazy route or provide it at the route's component — do not relax the check (§6) |
| Prism highlights nothing in a spec | `prism.ts` must be imported for its value, not only its side effects: the specs' global object is not the one Prism writes itself to, and the file assigns `globalThis.Prism` explicitly for that reason |
