# Answer Rendering

How the rebuilt Angular client at `enterprise-gpt-ui/` turns model output into readable, inert HTML: two independent layers that between them guarantee no raw markup reaches the DOM, a head/tail split that keeps a long answer smooth while it streams, the code-block chrome and its copy control, the diagram and math renderers that cost nothing until an answer needs them, the message-level Copy, and the scroll pinning that decides whether the page follows the newest text or leaves the reader where they are.

Audience: a developer extending the transcript renderer, reviewing the client's XSS posture, or debugging a transcript that renders, scrolls or highlights strangely. Read [Conversation Turn Lifecycle](../conversations/turn-lifecycle.md) first for `TurnStore`, the snapshot/timeline join these renderers consume, and [Frontend Foundation](frontend-foundation.md) for the build gates §6 leans on.

Companion to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference.

## 1. Overview

Three P0 stories landed together and closed phase P3 — the minimum viable chat replacement — and four more closed EP-6 on top of them:

| Story      | What it delivers                                                                                                                                                                               |
| ---------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **US-601** | Assistant text renders as markdown that cannot execute: raw HTML dropped at the parser, DOMPurify with a closed profile at the DOM boundary, and the whole renderer behind the lazy chat route |
| **US-602** | A streaming answer renders as a **stable head and a volatile tail** — two `<markdown>` instances — so a long turn re-parses one block instead of the whole answer per flush                    |
| **US-606** | The transcript follows the newest content while the reader is at the bottom, holds position when they are not, and offers frame `1b`'s 44 px jump-to-latest control back                       |
| **US-603** | Every fenced block gets frame `1b`'s `--code-head` bar and a Copy control — emitted by the renderer, served by **one** delegated listener on the transcript (§3.5)                             |
| **US-604** | Highlighting is tinted entirely from theme tokens, and two build gates hold it there: 26 measured contrast pairs and a ban on importing a Prism stylesheet (§7.1)                              |
| **US-605** | Mermaid diagrams and KaTeX math render when an answer contains them, behind `config.json` flags and dynamic imports the build gate polices in both directions (§8)                             |
| **US-607** | A Copy control on the prompt and on the settled answer, in a message footer that is now revealed on hover and focus rather than always visible (§9)                                            |

Eleven decisions shape everything here, and each looks removable until you know what it prevents:

1. **Two independent layers, not one.** marked has no `html: false`, so a `MarkedRenderer` override drops raw HTML tokens at the parser _and_ DOMPurify filters the result. Either one alone would be the whole boundary; together, a regression in one is not an exploit (§3).
2. **Application code performs no trust operation.** `ngx-markdown` calls `bypassSecurityTrustHtml` internally, so `src/` holds **zero** call sites and US-108's lint rule fails a build that introduces one (§3.3).
3. **Images are dropped, and that is policy.** Not a rendering gap: FR-51 says the client issues no third-party request at run time, and a remote image in a model answer — or in a tool result the model is repeating — is both such a request and a read receipt for whoever hosts it. The alt text renders in its place (§3.4).
4. **The streaming split is framework-free and its head only ever grows.** `splitStreamingMarkdown` lives in `domain/` and tests in Node. A boundary that could move backwards would re-parse the whole answer on the flush that moved it (§4).
5. **Pinning is driven by a `ResizeObserver`, not by the turn's signals.** The markdown renderer writes its output a microtask after change detection returns, so a scroll taken during the render phase measures the height the page had _before_ the text it is meant to follow (§5.2).
6. **The renderer rides the lazy chat chunk.** `provideChatMarkdown()` is provided at the `Chat` component, and `check-initial-chunk.mjs` fails the build if any of the stack becomes statically reachable from `main.ts`. This is the resolution of the PRD's last open question (§6).
7. **`div` and `button` in the profile are the one place the two layers stop being independent.** They exist for `renderer.code`, and they are safe _because_ layer one holds rather than regardless of it — so a spec drives forged chrome through the real pipeline as that regression's alarm (§3.2, §3.5).
8. **There is no light Prism stylesheet, and there will not be one.** `--code-bg` and `--code-head` are dark in _both_ themes by the design's own decision, so the palette is one hand-authored set of custom properties — which makes US-604's legibility criterion rest entirely on twelve colour values, and a criterion true only by inspection is a criterion that drifts. `check-tokens.mjs` measures it instead (§7.1).
9. **Diagrams and math are fetched at the moment content needs them, and never otherwise.** Both flags are deployment switches — the committed dev copy ships them `true`, and turning either off costs no rebuild — while both libraries are reached only through `await import(…)` behind injection tokens, and `check-initial-chunk.mjs` now asserts both that they are absent from the initial graph _and_ the chat chunk **and** that they are present somewhere — because every other check in that file passes when a library is simply gone (§8, §8.7).
10. **Math is captured at the token layer, not by a pass over rendered HTML.** marked eats LaTeX's delimiters and its escapes before a DOM exists, so `katex/contrib/auto-render` — the mechanism `ngx-markdown`'s own integration uses — could never have worked here, and would have rendered some expressions _wrong_ rather than not at all (§8.5).
11. **The message footer is revealed, not inserted.** `opacity` keeps the Copy control in the tab order where an `@if` would not, and keeps the geometry US-606's `ResizeObserver` follows from changing under the reader's pointer (§9.2).

### 1.1 Where each piece lives

| Concern                                                                                                                   | Where                                                                                                                                                                                                                                                                                                |
| ------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The renderer, the DOMPurify profile, the marked overrides, the code-block chrome                                          | [`features/chat/markdown/markdown-providers.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/markdown-providers.ts)                                                                                                                                                                       |
| Prism and the seven grammars the transcript highlights                                                                    | [`features/chat/markdown/prism.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/prism.ts)                                                                                                                                                                                                 |
| The head/tail split and `stripFenceInfo`                                                                                  | [`domain/markdown/streaming-split.ts`](../../enterprise-gpt-ui/src/app/domain/markdown/streaming-split.ts) — framework-free                                                                                                                                                                          |
| The two-renderer template and the caret                                                                                   | [`features/chat/transcript/assistant-turn.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/chat/transcript/assistant-turn.html)                                                                                  |
| The delegated copy listener, the confirmation, its live region                                                            | [`features/chat/transcript/transcript.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/chat/transcript/transcript.html)                                                                                              |
| Pinning, bottom detection, the jump control's state                                                                       | [`features/chat/transcript-pinning.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts)                                                                                                                                                                                         |
| The scroll container, the sentinel, the jump button                                                                       | [`features/chat/chat.html`](../../enterprise-gpt-ui/src/app/features/chat/chat.html), [`chat.scss`](../../enterprise-gpt-ui/src/app/features/chat/chat.scss)                                                                                                                                         |
| The deferred renderers, their configuration, the KaTeX `<link>`                                                           | [`features/chat/markdown/lazy-renderer-loader.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/lazy-renderer-loader.ts)                                                                                                                                                                   |
| The directive that draws diagrams and typesets math on a settled render                                                   | [`features/chat/markdown/markdown-extras.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/markdown-extras.ts)                                                                                                                                                                             |
| The **second** DOMPurify profile, for Mermaid's SVG only                                                                  | [`features/chat/markdown/diagram-svg.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/diagram-svg.ts)                                                                                                                                                                                     |
| The marked extension that captures LaTeX before marked rewrites it                                                        | [`features/chat/markdown/math-extension.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/math-extension.ts)                                                                                                                                                                               |
| Copying text, and the labels and window every copy control shares                                                         | [`core/clipboard/copy-text.ts`](../../enterprise-gpt-ui/src/app/core/clipboard/copy-text.ts)                                                                                                                                                                                                         |
| The message footer's Copy control                                                                                         | [`features/chat/transcript/message-copy.ts`](../../enterprise-gpt-ui/src/app/features/chat/transcript/message-copy.ts), [`.html`](../../enterprise-gpt-ui/src/app/features/chat/transcript/message-copy.html), [`.scss`](../../enterprise-gpt-ui/src/app/features/chat/transcript/message-copy.scss) |
| Styling for rendered output, including the code-block chrome, the diagram frame and the math (global — it is `innerHTML`) | [`src/styles/_markdown.scss`](../../enterprise-gpt-ui/src/styles/_markdown.scss), [`_prism.scss`](../../enterprise-gpt-ui/src/styles/_prism.scss)                                                                                                                                                    |
| The build gate that keeps the stack lazy, in both directions                                                              | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs)                                                                                                                                                                                                         |
| The contrast gate on the code surface, and the Prism-theme ban                                                            | [`scripts/check-tokens.mjs`](../../enterprise-gpt-ui/scripts/check-tokens.mjs), [`check-forbidden-apis.mjs`](../../enterprise-gpt-ui/scripts/check-forbidden-apis.mjs)                                                                                                                               |
| KaTeX's stylesheet and faces, copied into `public/vendor/katex`                                                           | [`scripts/copy-katex.mjs`](../../enterprise-gpt-ui/scripts/copy-katex.mjs)                                                                                                                                                                                                                           |
| Controllable observers for tests, and a host that drives the real pipeline                                                | [`src/testing/intersection-observer.ts`](../../enterprise-gpt-ui/src/testing/intersection-observer.ts), [`resize-observer.ts`](../../enterprise-gpt-ui/src/testing/resize-observer.ts), [`markdown-host.ts`](../../enterprise-gpt-ui/src/testing/markdown-host.ts)                                   |

## 2. Quick start

### 2.1 Rendering markdown on a chat surface

The providers are already installed on the chat route, so a component under it only binds the source text:

```html
<markdown class="assistant-turn__md" [data]="node.text" />
```

`MarkdownComponent` is a standalone import from `ngx-markdown`. The `assistant-turn__md` class is what the global stylesheet targets (§7) — without it the output renders unstyled.

### 2.2 Rendering a settled answer that may contain a diagram or an expression

Add the directive. It does nothing at all until the content is there and the flag is on, so there is no condition to write:

```html
<markdown class="assistant-turn__md" appMarkdownExtras [data]="node.text" />
```

`MarkdownExtras` is imported from [`features/chat/markdown/markdown-extras.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/markdown-extras.ts) and must sit on the **settled** render only — never on the streaming pair, for the reasons in §8.2. It depends on `LazyRendererLoader`, which `provideChatMarkdown()` supplies, so a surface cannot acquire one without the other.

### 2.3 Rendering markdown on a surface that is not chat

Provide the same factory on that route's component, never in `app.config.ts`:

```ts
@Component({
  // ...
  providers: [ConversationStore, TurnStore, provideChatMarkdown()],
})
export class Chat {}
```

Five rules that the build enforces rather than merely recommends:

- **Never import `ngx-markdown`, `marked`, `prismjs` or `dompurify` from code reachable by a static import from `main.ts`.** `npm run build` fails naming the library and the chunk (§6).
- **Never import `mermaid` or `katex` statically at all** — not from `main.ts`, and not from the chat chunk either. Both are reached through the `DIAGRAM_MODULE` / `MATH_MODULE` tokens, and the same gate fails the build from either root (§8.7).
- **Never call `bypassSecurityTrustHtml`.** `npm run lint` fails on the syntax, and the renderer already performs the trust behind its own sanitizer.
- **Never widen the profile in place.** `sanitizeChatMarkdown` passes its configuration per call rather than through `DOMPurify.setConfig`, so no other caller can inherit or widen it. A surface that genuinely needs different allowances gets its own function — which is exactly what the diagram sanitizer is (§8.3).
- **Never import a `prismjs/themes/` stylesheet**, in `src/` or in `angular.json`. `npm run lint` fails naming the file; the palette is token-driven and there is nothing to swap to (§7.1).

## 3. Two layers between model output and the DOM

Model output is untrusted input. It can carry text from an uploaded document, from an MCP tool result the model is quoting, or from a prompt another user wrote into a shared document — so "the model would not emit that" is not a control. Two layers stand between it and the DOM, and they fail independently.

### 3.1 Layer one — raw HTML never becomes markup

marked offers no `html: false` switch. `html` is the one renderer it routes both block-level and inline raw-HTML tokens through, so returning an empty string from it suppresses raw markup at the parser:

```ts
const renderer = new MarkedRenderer();
renderer.html = () => "";
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

`CHAT_ALLOWED_TAGS` is a **closed set** — every entry is something _this renderer_ emits, and a tag that is not on it cannot reach the DOM however it was spelled in the source. `CHAT_ALLOWED_ATTR` is traceable the same way: `href`/`title` to links, `class` to the `language-*` marker on a fenced block and to the chrome's own class names, `type`/`checked`/`disabled` to GFM task lists, `start` to an ordered list that does not begin at one, `align` to a table column.

Three consequences worth knowing:

- **`span` is not allowed, and Prism still works.** Highlighting runs on the DOM _after_ sanitizing, so Prism's own `span.token` markup needs no allowance. Anything that moves highlighting before the sanitizer breaks it.
- **Allowing `href` is not allowing any href.** DOMPurify's default URI test still rejects `javascript:` and friends, dropping the attribute and leaving the link text inert.
- **`div` and `button` are on the list, and they are the only two markdown syntax cannot produce.** They exist for US-603's code-block chrome, which `renderer.code` emits (§3.5).

That last one costs something specific, and it is stated here rather than glossed. Every other entry in the profile is safe **whether or not** layer one holds; `div` and `button` are safe **because** it holds. Model text cannot mint them today — raw HTML never becomes markup at all — but a regression in layer one (a marked upgrade routing some token kind past `renderer.html`, or someone relaxing the override) would let model text forge a `.md-code` wrapper that the transcript's delegated copy listener would then serve. So the widening buys chrome, not capability, **on the condition that layer one keeps holding** — and `markdown-security.spec.ts` drives a raw `<div class="md-code"><button class="md-code__copy">` through the real pipeline so that regression fails a test instead of shipping.

Note what did _not_ change: `span` stays out for the reason it always was, and a spec pins that an `onclick` or a `style` on a `<button>` is still stripped. Allowing an element is not allowing what it could carry.

**US-605 added nothing to either list, and that is the point.** A rendered diagram is an SVG document Mermaid produced, not markdown a model wrote; it never meets this profile, which contains no `svg` and never will. It is filtered by a **second, independent** DOMPurify call at the point Prism's own markup is written — after this profile has already had its say — so widening one could not widen the other (§8.3). The math extension emits only `div` and `code`, both already here.

### 3.3 Angular's own sanitizer is not in this path

Supplying a function through `ngx-markdown`'s `SANITIZE` provider **replaces** its use of Angular's `DomSanitizer` rather than adding to it. DOMPurify is therefore the only DOM-boundary layer, which is why its profile is closed rather than merely restrictive, and why the spec for it drives real payloads through the real pipeline instead of stubbing either half.

Nothing in the app holds a `SafeHtml`: stores hold markdown **source**, the renderer holds the HTML for the length of one write, and `bypassSecurityTrustHtml` appears nowhere in `src/`.

### 3.4 The renderer overrides, and what they are for

Beyond dropping HTML, four overrides and one option:

| Override                                                                                                                       | Why                                                                                                                                                                                                                                                                                                                                 |
| ------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `renderer.code` wraps every fenced block in frame `1b`'s chrome — a `--code-head` bar carrying the language and a Copy control | The affordance has to be **part of the rendered output**, not grafted on afterwards (US-603's first criterion), which is also what keeps it out of the streaming tail's per-flush re-render path. Details in §3.5                                                                                                                   |
| `renderer.checkbox` gives each box an `aria-label` — "Completed" / "Not completed"                                             | A task-list checkbox is a real, if disabled, form control, and a control with no label is a failure however inert it is. It is **named rather than hidden**, because the box is the only thing carrying done-ness: "done" and "todo" read identically, so hiding it would take the state away from a screen reader entirely         |
| `renderer.heading` shifts every level down **one**, clamped at `h6`                                                            | The conversation title is the page's `h1`, so a model's `#` would open a second one. Shifting by one slots the answer's hierarchy directly beneath the title; shifting by two would jump `h1` → `h3`, itself a heading-order failure                                                                                                |
| `renderer.image` renders the alt text, escaped                                                                                 | Images are dropped by the profile so the client issues no third-party request (§1, decision 3). Rendering `text \|\| title` keeps the description the model wrote instead of leaving a hole — and it is escaped here, because alt text is raw source and would otherwise re-enter the pipeline as markup for the sanitizer to judge |
| `breaks: true`                                                                                                                 | Model output leans on single newlines for structure far more than prose markdown does, and the transcript rendered them literally before US-601. Collapsing them now would silently reflow answers users had already read                                                                                                           |

The heading override is a `function`, not an arrow, and that is load-bearing: marked binds the live renderer as `this`, so `this.parser.parseInline(tokens)` uses the parser carrying **these** options. The static `Parser.parseInline` would build a fresh one with marked's defaults — quietly exempting everything inside a heading from layer one's raw-HTML suppression. A spec drives `## <script>…</script> heading` through the pipeline for exactly that reason.

`aria-label` is not in `CHAT_ALLOWED_ATTR` and survives anyway: DOMPurify allows ARIA attributes by default (`ALLOW_ARIA_ATTR`). Both the task-list checkbox and the copy control's per-language name depend on that, so switching `ALLOW_ARIA_ATTR` off would silently strip two accessible names. Do not "fix" the omission by listing `aria-label` either — that list is for the attributes the renderer's _markup_ needs.

### 3.5 The code-block chrome and its copy control (US-603)

`ngx-markdown` ships a `clipboard` directive, and it was **rejected** because it fails all four of the story's own constraints: it needs a `clipboard.js` dependency the app does not carry, it grafts its toolbar on with `document.createElement` _after_ the render — the thing criterion 1 forbids — it copies `innerText` rather than the source, and it would rebuild a component and a `ClipboardJS` instance per `<pre>` on every one of the tail's ~16 ms flushes. So the chrome is emitted by `renderer.code` instead:

```html
<div class="md-code">
  <div class="md-code__head">
    <code class="md-code__lang">ts</code>
    <button class="md-code__copy" type="button" aria-label="Copy ts code block">
      Copy
    </button>
  </div>
  <pre class="language-ts"><code class="language-ts">…</code></pre>
</div>
```

**The info string is restricted, not escaped.** Only a leading `[\w+#.-]+` becomes the `language-*` class, so ` ```ts"onload=… ` is simply not a language and cannot reach an attribute in the first place — a stronger position than escaping it into one. The marker goes on the `<pre>` **as well as** the `<code>`: that is Prism's canonical markup, and what `_prism.scss` matches on.

**Copying is one delegated listener on the transcript host** — the only thing that survives the streaming tail replacing the button within a flush, and the alternative to a listener per `<pre>`. It reads the block back off the DOM, where Prism's highlighting has added elements but no characters, so `textContent` is the source marked was given, less the trailing newline marked appends. A click on anything else in the transcript — including `StoppedCard`'s own Copy, a real component with its own handler — does not match and falls straight through.

**The confirmation takes two channels, because neither serves both audiences:**

| Channel                                                     | Who it serves       | Why not the other one alone                                                                                                                                 |
| ----------------------------------------------------------- | ------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The visible label swaps to "Copied" for 2 s                 | Sighted users       | Written straight to the DOM, since the button lives inside `innerHTML` where Angular has nothing to bind                                                    |
| A separate `role="status"` region says "Code block copied." | Screen-reader users | The label is not the accessible name (below), so a name that no longer changes announces nothing — and the region survives the tail re-rendering the button |

Three details in that are load-bearing:

- **Each button is named `Copy <language> code block`, not "Copy".** An answer with six code blocks would otherwise offer six controls with identical names and nothing to tell them apart in a rotor.
- **The accessible name follows the visible label into "Copied" and back.** Otherwise SC 2.5.3 (Label in Name) fails for those two seconds and a speech-input user saying "click Copied" has nothing to activate.
- **The live-region message is set one render late, and that is not a hedge.** A live region announces a _transition_, not a value, and change detection is scheduled rather than synchronous — clearing and re-setting the signal in one task leaves the binding's previous value untouched, so copying a second block inside the first's two-second window would write nothing to the DOM and say nothing. The write is deferred through `afterNextRender`, and a spec watches the region with a `MutationObserver` for exactly this, because reading its text would pass on the stale value.

The copy region is deliberately **separate** from the transcript's existing `role="status"`, which is emptied for the length of a turn ([turn lifecycle §8](../conversations/turn-lifecycle.md#8-rendering-the-turn)) — a copy can happen mid-turn, and sharing one region would either swallow the confirmation or break that rule. A denied clipboard permission leaves the button exactly as it was; there is nothing actionable to say beyond "it did not happen".

Two limits are recorded rather than hidden, both in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table:

- **The control has no `bi-copy` glyph**, where the board draws one. The icon set is an SVG sprite consumed through `<use>`, and admitting `svg`/`use` to the profile is a much larger surface than a Copy control is worth. Nothing is planned against it.
- **A code block that overflows its column scrolls by pointer only.** The `<pre>` carries no `tabindex`, so a keyboard user cannot reach the rest of a long line (WCAG 2.1.1). This pre-dates US-603, which now owns the chrome around it; **US-1401** owns keyboard operability and the axe gate, and closing it needs `tabindex` in `CHAT_ALLOWED_ATTR` plus a name for the region.

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

Fences are paired the way CommonMark pairs them — a block closes only on a later fence of the _same_ character that is at least as long — so a four-backtick block can quote a three-backtick one without the inner pair terminating it. The delimiter stays with the head.

> **The head only ever grows.** Every rule above is written to preserve that, and a spec asserts it across every prefix of several fixtures. A boundary that could move backwards would hand the renderer a _different_ head, re-parsing the whole answer on the flush that moved it — the failure mode the split exists to avoid, arriving intermittently.

Offsets are byte-for-byte, so the source must use `\n` line endings. The SSE codec guarantees that; `\r\n` would simply never match a boundary and the answer would render as one block.

### 4.2 The tail is not highlighted

`stripFenceInfo` removes the info string from every line-start fence in the tail, so a still-arriving code block has no `language-*` class and Prism leaves it alone. Mid-stream that is the honest rendering — the fence may be half a line of code, and highlighting it against a grammar it does not yet satisfy produces flickering nonsense. The head keeps its info strings and highlights normally.

### 4.3 Settling

When the turn settles, the caret index goes to `-1`, the pair collapses, and the whole node renders once from the **untouched** source — fences, languages, highlighting and all. A spec asserts that this settled render is character-identical to a single full render of the same source, which is what makes the split an optimisation rather than a second rendering mode.

An answer replayed from a reopened conversation (US-410) takes this same settled path and no other: replay folds the stored text through the vendored reducers into the shape a settled live turn holds, so it arrives here as one already-complete text node ([turn lifecycle §7.2](../conversations/turn-lifecycle.md#72-a-replayed-answer-goes-through-the-same-reducers-as-a-live-one)). What it lacks is activity cards, not markdown.

### 4.4 The caret, and why the pair is wrapped

Frame `1b`'s 8×17 px `--accent` caret rides whichever renderer ends the text — the tail normally, the head on the rare flush where the split leaves nothing over — and is drawn as a CSS pseudo-element, because the output is `innerHTML` and there is no template position left inside it for a span. Which _element_ inside that renderer it attaches to is a set of cases in `_markdown.scss`: a trailing list puts it on the last item, and since US-603 a trailing code block puts it on the `pre` rather than on the `.md-code` wrapper around it (§7).

The two renderers are wrapped in one `.assistant-turn__live` element so the pair counts as a single child of the content column. As two children the column's 10 px gap would space them apart, then close to a paragraph margin the moment the turn settled into a single render — a visible jump at the end of every answer.

## 5. Following the newest content (US-606)

[`TranscriptPinning`](../../enterprise-gpt-ui/src/app/features/chat/transcript-pinning.ts) is a directive on the scroll container itself, so the host element is both the thing scrolled and the observer's root.

### 5.1 Bottom detection

An `IntersectionObserver` watches a zero-height sentinel with `rootMargin: '0px 0px 80px 0px'` — the tolerance that treats a reader a line or two short of the end as still following. A scroll listener was rejected: it runs on the main thread for every frame of a flick and would still have to measure to answer the only question asked here.

The sentinel is **passed into the directive rather than queried**, and it lives outside the body's `@if` chain as an unconditional last child. One element, one observation, across the error / transcript / empty-state swaps — a sentinel inside a branch would be replaced on every swap and leave the observer watching a detached node.

`_atBottom` starts `true`. A real observer's first record is asynchronous, and treating "not yet reported" as "scrolled up" would flash the jump control on every mount.

### 5.2 Following is a `ResizeObserver`, not a signal effect

This is the decision most likely to be "simplified" back into a bug. The answer is not in the DOM when the turn's signals settle: the markdown renderer writes its output a microtask _after_ change detection returns, so a scroll taken during the render phase measures the height the page had before the text it is meant to follow — the transcript trails the answer by one flush, visibly.

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

The PRD's last open question and [the build order's ⛔ gate at US-601](../prd/enterprise-ui-rebuild-build-order.md) both came due at the _start_ of this story: pay for `ngx-markdown` + marked + Prism + DOMPurify in the initial bundle and re-baseline, or move the transcript renderer behind the lazy chat route.

**It went behind the lazy route.** `provideChatMarkdown()` is provided on the `Chat` component, and `check-initial-chunk.mjs` gained a FORBIDDEN entry for `ngx-markdown|marked|prismjs|dompurify` beside the ones US-605 and US-203 already had, so a static import from eagerly reachable code fails the build naming the library and the chunk.

|                             | Initial raw   | Initial transfer | `chat` chunk  | `styles`     | Budget (warn / error) |
| --------------------------- | ------------- | ---------------- | ------------- | ------------ | --------------------- |
| Before EP-6 (end of US-501) | 648.75 kB     | 157.96 kB        | 48.60 kB      | 60.50 kB     | 660 kB / 720 kB       |
| After US-601/602/606        | 660.24 kB     | 161.01 kB        | 179.47 kB     | 62.08 kB     | **665 kB / 720 kB**   |
| After EP-5 and US-603       | 661.36 kB     | 161.19 kB        | 198.51 kB     | 63.19 kB     | 665 kB / 720 kB       |
| After EP-6 and US-701       | 663.46 kB     | 163.36 kB        | 179.04 kB     | 63.96 kB     | **670 kB / 720 kB**   |
| **After EP-7**              | **663.45 kB** | **163.11 kB**    | 179.04 kB     | 63.96 kB     | 670 kB / 720 kB       |

The whole **124.8 kB** markdown stack is in the lazy chat chunk and none of it is in the initial graph — marked 42.5 kB, Prism 40.8 kB, DOMPurify 29.0 kB, `ngx-markdown` 12.5 kB.

**The ~11.5 kB the initial graph grew by at US-601 is not the markdown libraries.** It is Angular's own `DomSanitizerImpl` — `ngx-markdown`'s `MarkdownService` injects `DomSanitizer`, which lands in a chunk shared between initial and lazy code — plus ~1.6 kB of global CSS for the rendered output. Unavoidable without dropping `ngx-markdown`, and the reason the **warning** line moved 660 → 665 kB. The **error ceiling has never moved from 720 kB**, which is the number that matters.

**Nothing in EP-6's closing three is in the initial graph either.** The `chat` chunk _fell_ from 198.51 to 179.04 kB, because esbuild moved shared code into chunks the route now pulls in beside it rather than into the route's own; the initial figure grew 2.1 kB, which is US-701's screen and the global CSS for the diagram frame, the math and the message footer. The warning line was re-stated 665 → **670 kB** at US-701 and the `styles` budget is unchanged at 65 / 80 kB.

**EP-7's four remaining stories cost nothing initial**, which is what a lazy route is for: paging, the favourites filter, bulk delete and the order statement are all inside the `conversations` chunk, and the one new component among them (`DeleteConversationsDialog`) rides it too. The initial figure is level at 663.45 kB raw, and transfer fell 0.25 kB on compression noise. No re-baseline, no threshold change.

**Mermaid and KaTeX are in the build and in neither of those numbers.** KaTeX is one 267.72 kB chunk plus a 23.3 kB stylesheet and 20 woff2 faces served from `public/vendor/katex`; Mermaid is a family of chunks — a core plus one per diagram type, roughly 1.5 MB in total — because its own build loads each diagram grammar on demand. A reader fetches the core and the types their answer actually uses, and only when the flag is on and matching content appears. §8.7 is the gate that keeps that true.

Three smaller build facts came with all this: `@types/prismjs` is a devDependency; `angular.json`'s `allowedCommonJsDependencies` now lists Mermaid's CommonJS transitive dependencies (`cytoscape-fcose`, `cytoscape-cose-bilkent`, `@braintree/sanitize-url` and four `dayjs` plugins) beside `prismjs`, because the builder warns on every such import otherwise; and `npm run assets` gained `assets:katex`, so `public/vendor/` — gitignored, like `public/fonts` and `public/icons` — is generated rather than committed.

## 7. Styling output the templates never see

Rendered markdown is written as `innerHTML`, so it carries no `_ngcontent` attribute and **no component stylesheet can match it**. Its rules are global, in [`src/styles/_markdown.scss`](../../enterprise-gpt-ui/src/styles/_markdown.scss), scoped by the `.assistant-turn__md` class on the host — nothing there applies outside a rendered answer. The import sits after `prism` in the `styles.scss` chain, whose code-block rules it defers to.

The code-block chrome at the foot of that file is the one deliberate exception to the `.assistant-turn__md` scoping: `.md-code` is emitted by `renderer.code`, so it exists wherever this renderer does, and a second markdown surface should get the same block without inheriting the transcript's body type.

Four rules in it are load-bearing:

- **Every block carries its spacing as a top margin only**, and no edge is reset. While a turn streams the growing node is two renderers, so an edge rule would space head and tail differently from the single render that replaces them at settle.
- **No `overflow` on `pre`.** `_prism.scss` sets `overflow: auto` at the same specificity and is imported first, so anything set here would win and clip every code line wider than the column — unreachable by mouse, keyboard or touch.
- **The `pre` gave its border and radius to the `.md-code` wrapper**, and kept only `margin: 0`. The head bar and the body share one rounded outline, and only the element around both can draw it; the wrapper also took over the block's top margin, so the rhythm is unchanged from when `pre` carried both. The wrapper is `overflow: hidden`, which is what clips the body's corners to that radius.
- **The wrapper states the code surface itself** rather than leaving it to `_prism.scss`, which matches only on `language-*`. Prism does eventually mark an unlabelled block `language-none`, but it runs _after_ the write — so a fence with no info string, which is **every fence in the streaming tail** once `stripFenceInfo` has been past it (§4.2), would otherwise paint one frame on the page background with the head bar floating above it.

The caret gained a code-block case for the same reason. Frame `1b`'s caret rides the last rendered block (§4.4), and a `.md-code` wrapper is not a text box: the generic rule drew it _below_ the code and inside the rounded box, on the wrapper's own background, for the whole of a streaming fence. It now targets the `pre` inside a trailing `.md-code`, with the wrapper itself opted out of the generic rule — the same shape as the existing list rules, and the reduced-motion block carries the same case.

Highlighting itself is token-driven: no Prism stylesheet is imported, because `--code-bg` is dark in **both** themes and neither of Prism's shipped themes matches the design. A theme change re-tints code on the same paint as `data-bs-theme` flipping. The head bar follows that rule — `--code-head-fg` and `--code-head-muted` are transcribed from the Transcript board's literals (`#B9CBDC`, `#8FA9C0`) and sit **identical in both theme maps**, because `--code-head` is dark in both and text on it never flips. Prism's grammar list is a deliberate floor — TypeScript, C#, Python, Bash, JSON, YAML, SQL, on top of the markup/CSS/clike/JavaScript core already carries — because each one is bytes in the chat chunk and an unlisted language renders as an unhighlighted block rather than an error.

US-605's rules sit in the same file and follow the same logic. There is **no styling for `.md-diagram` on its own** — an undrawn diagram is an ordinary code block, which is what makes the flag-off path and the failed-to-parse path one path — and `.md-diagram--rendered` is what hides the source, lightens the head bar and frames the figure. The math rules only undo `code`'s own chrome so a typeset expression reads as the prose around it, and give `.katex-display` its own `overflow-x`, since a wide equation would otherwise push the transcript column sideways.

### 7.1 Code theming, and the two gates that hold it (US-604)

US-604 asked for a Prism stylesheet that swaps between a light and a dark variant. **There is none, and there will not be one** — agreed with the product owner before implementation, and recorded in the [build order](../prd/enterprise-ui-rebuild-build-order.md)'s deviation notes. `docs/design/project/theme.css` fixes `--code-bg` and `--code-head` **dark in both themes** (`#0B1F33` / `#081827` and `#122A42` / `#0F2438`), `check-tokens.mjs` already enforced that parity against the design bundle, and §9 of the design system's rules gives the board the last word on everything but accessibility. So there is no light surface to theme _for_, no `<link>` to toggle, and — which is why the story's third criterion came free — no light-styled first frame anyone could see.

That leaves a criterion resting entirely on twelve colour values, and a criterion true only by inspection is a criterion that drifts. The work of the story is therefore the enforcement, in two scripts:

| Gate                       | What it does                                                                                                                                                                                                                                                                                                                                                                                                                                 | Why it is not obvious                                                                                                                                                                                                                                                                                                                                                              |
| -------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `check-tokens.mjs`         | Measures **26 code-surface pairs** — 13 in each theme map — and fails below WCAG 2.1 AA. The seven Prism token colours and `--code-fg` on `--code-bg`; the two head-bar colours on `--code-head`; the same two on `--code-head` **composited with the head bar's white-8 % hover wash**; and `--accent` on both surfaces at SC 1.4.11's **3:1**, because the focus ring and the streaming caret are non-text marks. Worst case is **4.72:1** | The hovered figure is the one that matters and the unhovered one overstates it: `.md-code__copy` sits on that wash while hovered _or_ confirming, which turns 5.99:1 into 4.72:1 in light. `--accent` is measured at 3:1 rather than the page's `--focus-ring`, which is the wrong token on a dark code surface — the stylesheet says so in prose, and this is that claim measured |
| `check-tokens.mjs`, again  | Scans `_prism.scss` and `_markdown.scss` for `var(--code-*)` and fails on any the pair list does not measure                                                                                                                                                                                                                                                                                                                                 | The pair list is hand-written. Without this, repointing `.token.keyword` at a different token would leave the gate measuring a colour nothing renders — green, and testing nothing                                                                                                                                                                                                 |
| `check-forbidden-apis.mjs` | Bans any `prismjs/themes/` import, **unanchored**, and scans `angular.json` as well as `src/`                                                                                                                                                                                                                                                                                                                                                | The specifier is spelled at least five ways — bare, `~`-prefixed, `node_modules/…`, through `url()`, and as an `angular.json` `styles` entry. That last one is the route `ngx-markdown`'s own README prescribes, it lives outside `src/`, and no other gate can see it                                                                                                             |

Both run under `npm run lint`. A palette edit that breaks contrast is told _which_ colour it broke and by how much, rather than shipping something that only looked right in the editor.

## 8. Diagrams and math, loaded only when an answer needs them (US-605)

Mermaid and KaTeX together are several times the size of the whole initial bundle, and most answers contain neither. So both are behind a `config.json` feature flag, both flags ship **`false`** in `public/config.json`, and even with a flag on nothing is fetched until matching content appears:

```json
"features": { "diagrams": false, "math": false, "rawStreamCodec": false }
```

**`ngx-markdown`'s own `mermaid` and `katex` integrations are not used** — exactly as US-603 declined its `clipboard` directive, and for the same class of reason. They resolve from `window` globals and throw from an unawaited async method if either is missing; `extendsRendererForMermaid` mutates the _shared_ `MarkedRenderer` and marked's global state permanently, so one component enabling it enables it for every instance in the injector; it interpolates the diagram source **unescaped**, mangling any label containing `<`; and `mermaid.run()` has no error hook at all — it wipes the element and paints its own error graphic, taking the source off the page, which puts the story's fourth criterion out of reach on that path. The branch therefore lives in the renderer this app already owns.

Four files carry it:

| File                                                                                                        | Role                                                                                                                                  |
| ----------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| [`lazy-renderer-loader.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/lazy-renderer-loader.ts) | Reads the flags, performs the dynamic imports, configures Mermaid and KaTeX, serializes diagram renders, and links KaTeX's stylesheet |
| [`markdown-extras.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/markdown-extras.ts)           | The `markdown[appMarkdownExtras]` directive: finds the work in a settled render, draws it, redraws it on a theme flip                 |
| [`diagram-svg.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/diagram-svg.ts)                   | The second DOMPurify profile, for Mermaid's SVG only                                                                                  |
| [`math-extension.ts`](../../enterprise-gpt-ui/src/app/features/chat/markdown/math-extension.ts)             | A marked extension that captures LaTeX at the token layer                                                                             |

### 8.1 The renderer's whole involvement is one class

`renderer.code` stays a **pure function of the token** and reads no configuration. Its entire change for this story is adding `md-diagram` to the otherwise-identical `.md-code` chrome when the info string is `mermaid`.

That one class is what makes two of the story's criteria cost nothing. With the flag off nothing runs, so the block simply **is** a code block — there is no disabled branch to get wrong and no error to report. And because the escaped source was never removed, a diagram that fails to parse can show it: `.md-diagram--rendered` **hides** the `<pre>` rather than removing it, which makes the source the render cache and the failure fallback at once, and keeps US-603's Copy control copying the source rather than nothing.

### 8.2 It runs on the settled render, never on the stream

`appMarkdownExtras` rides the settled `<markdown>` only, and both halves of that are forced:

- **The tail cannot carry it.** `stripFenceInfo` has removed its info strings (§4.2), so a mid-stream `mermaid` fence has no language left to match.
- **The head must not.** Its content is replaced wholesale whenever a block closes, and re-laying-out an SVG per flush would fight both the ~16 ms batch cadence and US-606's `ResizeObserver` (§5.2).

So a diagram reads as a `mermaid`-labelled code block while the answer arrives and becomes a picture when the turn settles — the contract US-602 already set for highlighting. It is recorded as an interim behaviour in the build order with **nothing planned against it**: per-flush SVG layout is the cost this design exists to avoid.

The directive hangs off `MarkdownComponent.ready` rather than a lifecycle hook, because that is the only signal that the output has actually been written — `ngx-markdown` assigns `innerHTML` a beat after change detection returns. It sits on the same element for the mundane reason that `MarkdownComponent`'s `ElementRef` is not public.

### 8.3 Two sanitizer profiles, and why they stay apart

`CHAT_ALLOWED_TAGS` and `CHAT_ALLOWED_ATTR` **gain nothing** from this story. Mermaid's SVG is not markdown: it is written _after_ the markdown profile has had its say, exactly as Prism's markup is, so it gets its own DOMPurify call in `sanitizeDiagramSvg` — over DOMPurify's SVG profile, with two subtractions and one deliberate retention.

| Decision                                | Reason                                                                                                                                                     |
| --------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `a` and `href`/`xlink:href` **removed** | Mermaid's `click A "https://…"` syntax would otherwise mint a link the user never wrote, to an origin FR-51 says this client never contacts                |
| `foreignObject` **removed**             | It is the one element that opens an HTML document inside an SVG one. `htmlLabels: false` already stops Mermaid emitting it; this is the belt to that brace |
| `style` **kept**                        | It is already in DOMPurify's SVG profile, and Mermaid scopes its CSS to the render id and emits it inline — removing it would leave every diagram unstyled |
| `RETURN_DOM_FRAGMENT`                   | Assigning the result to `innerHTML` would parse it a second time, and a second parse is a second chance for markup the first one neutralised               |

**Keeping `style` is what makes the CSS pass necessary rather than optional.** DOMPurify has no CSS parser: it hands a `<style>` body and a `style` attribute through verbatim, and a `<style>` inside _inline_ SVG is document-scoped, not SVG-scoped. This is not hypothetical for model-authored input — Mermaid's `themeCSS` is a directive-settable key, so `%%{init: {"themeCSS": "…"}}%%` inside a fence reaches that element, and Mermaid's own guard is a brace-balance counter that explicitly permits `url(https://…)`. Two hooks therefore strip `@import` and every non-fragment `url(` from both, which is what makes FR-51 true at this boundary rather than borrowed from a dependency. A local `url(#fragment)` survives, because that is how a diagram finds its own arrowhead markers.

The hooks are removed in a `finally`. They are **global to the DOMPurify instance `sanitizeChatMarkdown` shares**, so leaking one would put a CSS rewriter into the markdown path; a spec asserts the markdown profile behaves identically afterwards.

### 8.4 Mermaid is configured to draw, not to run

`parse()` runs first and a source that fails it never reaches `render()` — Mermaid's own failure mode is to replace the element with an error graphic, which would take the source off the page. Beyond that:

- `securityLevel: 'strict'`, `htmlLabels: false` (in `flowchart` too), `suppressErrorRendering`, `logLevel: 'fatal'`, and the `bindFunctions` it hands back is never called. Not `'loose'`, and deliberately not `'sandbox'` — that renders into an iframe, which breaks sizing and theming both.
- `maxTextSize: 20_000` and `maxEdges: 200`. Model output is attacker-influenceable through uploaded documents and tool results, and diagram layout is main-thread work; these are the ceiling on what one answer can spend.
- **The palette comes from `getComputedStyle` at render time**, not from transcribed hexes, so a diagram matches whatever `data-bs-theme` currently resolves to and there is nothing new for `check-tokens.mjs` to police. Every empty value is **dropped** rather than passed: a renamed token and a test environment both hand back `''`, and Mermaid given `fill: ""` draws invisible nodes.
- **Renders are serialized inside the shared renderer**, not in the caller. Mermaid holds its configuration in module state and `parse` re-applies the current diagram's own `%%{init}%%` directives, so two interleaved renders can swap each other's settings — and a transcript with diagrams in three replayed turns mounts three directives at once against this one renderer. A queue in the caller would only order that caller's work.
- **A theme flip re-draws every diagram on screen.** An SVG has its colours baked in and cannot re-tint the way a code block does. That is possible only because §8.1 keeps the source, and it runs from an `afterRenderEffect` rather than an `effect` — a plain effect runs _before_ Angular has updated the DOM, so a flip landing in the same pass as a `[data]` change would query the previous tree.
- A drawn diagram is wrapped in a `<figure role="img">` with a name, and the SVG inside it is `aria-hidden`. DOMPurify strips Mermaid's own `role`, and what is left is a bag of loose `<text>` runs; the source stays reachable through the Copy control above it.

A failure leaves the source visible and appends frame `1h`'s warn-tinted notice beneath it — "This diagram could not be drawn. Its source is above." The notice's text stays `--bs-body-color` rather than `--warn`, which measures 3.99:1 on `--warn-bg` in light; `TurnNoticeCard` already inherits the body colour on the same surface for the same reason, and the fix belongs upstream in the design bundle rather than as a second local override.

### 8.5 Math is captured at the token layer

A DOM pass over rendered output cannot work here, and the review proved that by running the real pipeline rather than arguing it. Three separate things defeat `katex/contrib/auto-render`:

1. **`\(`, `\)`, `\[` and `\]` are in marked's inline escape class**, so `\(x^2\)` reaches the DOM as `(x^2)`. Delimiters configured for those forms match nothing, ever.
2. **`breaks: true` puts a `<br>` between the markers** of a `$$…$$` opened and closed on separate lines — the commonest display form there is — and auto-render only joins _consecutive text nodes_, so an element between them ends the run.
3. **marked strips LaTeX's own escapes.** `\{`, `\}`, `\_`, `\%`, `\&`, `\#` and the `\\` row separator vanish, so sets, `aligned` bodies and escaped subscripts render **wrong** rather than not at all — which is worse, because nothing reports it.

A tokenizer runs before every one of those rules and takes the source verbatim, which is the only point at which the expression still exists as the model wrote it. It emits an escaped placeholder (`div.md-math` for a block, `code.md-math` inline — a `code` because a `div` there would be reparented out of its paragraph) that `MarkdownExtras` later hands to `katex.renderToString`. The extension is registered **unconditionally**: with the flag off the placeholder simply renders as the source text, and KaTeX is still behind its dynamic import.

Four details in it are load-bearing:

- **The block tokenizer's `start` is line-anchored.** `start` tells marked where a block _may_ begin and marked cuts the paragraph there, so an unanchored match would split "an expression: `$$x^2$$` inline" into three lines that `breaks: true` then joins with `<br>`.
- **There is no single-`$` form.** "it costs $5 to $10" is the classic false positive, and prose is far commoner in an answer than inline math. `$$` and `\[` are display wherever they appear; `\(` is inline — KaTeX's own auto-render defaults.
- **`trust: false` and `throwOnError: false` are stated rather than defaulted.** `trust: true` enables `\href`, `\url` and `\includegraphics`; `throwOnError: false` is the math half of criterion 4, rendering a malformed expression as its own source in place instead of aborting the pass and taking the answer's other expressions with it.
- **`errorColor` is omitted rather than passed empty** when `--fail` cannot be resolved. KaTeX takes `''` literally and would render a malformed expression indistinguishably from a good one — the exact failure the option exists to make visible.

A `$$` inside a fenced block is untouched for free, because a fence is a different token. `macros: {}` is a fresh object per call, since KaTeX mutates the macros it is given and a shared one would carry an answer's `\newcommand` into the next answer.

### 8.6 KaTeX's stylesheet is copied, not bundled

[`scripts/copy-katex.mjs`](../../enterprise-gpt-ui/scripts/copy-katex.mjs) copies `katex.min.css` (23.3 kB) and **woff2 faces only** into `public/vendor/katex`, and `LazyRendererLoader` appends a `<link>` to it the moment the first expression is typeset. Three reasons it is not in `styles.scss`: that bundle is eagerly loaded and has little headroom under its 65 kB budget, math is a flag most deployments leave off, and everything must be same-origin (FR-51).

Two details are worth knowing when it misbehaves:

- The link is checked **against the document**, not a field. `LazyRendererLoader` is route-scoped, so leaving `/chat` and coming back would otherwise append a second link per visit.
- The `<link>` carries an `error` listener that logs _"The KaTeX stylesheet is missing: … Run `npm run assets`."_ `public/vendor/` is generated and gitignored, so a deployment that ships `dist` without running the asset step renders math **unstyled** — worse than not rendering it, and silent without that listener.

woff2 alone is deliberate: KaTeX declares each face three times with a `format()` hint, so a browser picks the first it supports and never requests the other two. `npm run assets` runs `assets:fonts`, `assets:icons` and now `assets:katex`, and the build's `prestart`/`prebuild`/`pretest` hooks run it for you.

### 8.7 What the build gate now checks, in both directions

`check-initial-chunk.mjs` had entries for `mermaid` and `katex` from US-108, and both were passing for the wrong reason. US-605 added the two things they could not do:

- **A second traversal rooted at the chat chunk.** A static `import 'mermaid'` from the chat route lands in the _lazy_ chunk — off the initial graph, green under the old check, and downloaded by every reader who opens a conversation. Each rule now names the roots it must also stay out of.
- **A positive presence assertion.** Every other check in that file passes when a library is simply absent, so deleting the dynamic import would have turned the gate green _and_ silent. `expect: true` says the library has to be in the build somewhere, and the failure message says what to do if the import was removed on purpose.

## 9. Copying a prompt or a response (US-607)

There are now three copy controls on a chat screen — a code block's, the stopped card's, and this one — so the label, the confirmation label and the confirmation window live together in [`core/clipboard/copy-text.ts`](../../enterprise-gpt-ui/src/app/core/clipboard/copy-text.ts) and all three read them. Two confirmations of visibly different lengths read as one of them having failed; `copyText` itself returns a boolean rather than throwing, because an insecure context (no `navigator.clipboard` at all) and a refused permission are the same non-event to the reader.

### 9.1 Why this one needs no live region

[`MessageCopy`](../../enterprise-gpt-ui/src/app/features/chat/transcript/message-copy.ts) is a **real template-bound button**, which is the whole difference from US-603's:

|                | The code block's control (§3.5)                                            | The message footer's                                                                           |
| -------------- | -------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| Where it lives | Minted inside rendered `innerHTML`, where Angular has nothing to bind      | A component, bound normally                                                                    |
| How many       | Six identically-named ones in an answer with six blocks                    | One per message, named `Copy prompt` / `Copy response`                                         |
| Lifetime       | Replaced by the streaming tail within a flush                              | Exists only on a **settled** turn                                                              |
| Confirmation   | Visible label written to the DOM **and** a separate `role="status"` region | The name changes on the element the reader just activated — `StoppedCard`'s existing reasoning |

The visible label and the accessible name are derived from the **same two constants**, so SC 2.5.3 (Label in Name) holds by construction rather than by convention: both become "Copied" together, and both come back together. Re-copying restarts the window rather than letting the first timer cut the second confirmation short, and destruction cancels it — including the window between the click and the clipboard settling, which has no timer to cancel yet.

Unlike the code-block control, this one **does** carry the board's `bi-copy` glyph. It is a component, so the icon comes through the `<app-icon>` sprite rather than needing `svg`/`use` in the sanitizer profile.

### 9.2 The footer's gate inverted, and the reveal

US-504 shipped the footer conditional on there being token counts. That is now the other way round: **the footer is unconditional for a settled turn and the counts are the conditional child**, because Copy is offerable whether or not a `Finished` ever arrived — a stopped, cut-off or replayed turn has text worth copying and no counts.

**The control itself is gated on there being text**, and that is not defensive tidying: a turn can settle empty (a cut-off that died after its first activity), and `writeText('')` **succeeds** — an ungated control would confirm having wiped the reader's clipboard.

The reveal is `opacity` with three selectors, never an `@if`:

- `:hover` — what the board draws.
- `:focus-within` — opacity keeps the control in the tab order, which is what makes it reachable at all; the board's hover-only annotation is unreachable by keyboard, which is why US-504 shipped the footer always-visible until this story existed.
- `:has(.message-copy--copied)` — clicking can leave both the pointer and focus elsewhere, and a "Copied" nobody can see is not a confirmation.

`@media (hover: none)` shows it outright, since hidden-until-hover means nothing on a touch screen. An `@if` would also have changed the footer's height on hover, moving the very geometry US-606's `ResizeObserver` follows (§5.2).

**The optimistic user bubble renders an empty footer** for the same reason: without it the prompt row grows by ~28 px the moment its turn settles and the whole column below shifts under the reader. It carries no control, because the turn is not complete.

The assistant control copies `snapshot.text`, **not** a re-join of `renderedNodes()`. Those slices exist to be interleaved with activity cards in arrival order; they happen to cover the whole text today, but that is the timeline's contract to change, and a copy that silently lost a block would look like a clipboard fault rather than a rendering one.

## 10. Testing

The suite stood at **1099 specs across 90 files** when EP-6 closed, green alongside `npm run lint` and `npm run build`. (EP-7 took it to 1153 — [Conversation Library §7](conversation-library.md#7-testing).)

| Area                             | Spec                            | Notable cases                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| -------------------------------- | ------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The sanitizing pipeline          | `markdown-security` (25)        | The three payloads the story names — `<img src=x onerror=…>`, `<script>`, a `javascript:` href — driven through the **real** renderer and profile rather than a stub, because a spec that mocked either layer would prove nothing; raw HTML dropped with the sanitizer switched off, proving layer one alone; data attributes dropped and the `language-*` class kept; images dropped; `span` refused; the renderer overrides, including **raw HTML inside a heading**, which regressed once when the override built its own parser                                   |
| The code-block chrome            | `markdown-security` (in the 25) | The head bar naming the language and the marker landing on the `pre` as well as the `code`; an unlabelled block still offering the control; the body escaped rather than parsed; two blocks getting two _distinct_ accessible names; an info string that is not a language refused rather than escaped; and — the one that guards §3.2's coupling — **raw `.md-code` chrome in the source refused**, so model text cannot aim the delegated listener                                                                                                                  |
| Code theming                     | `code-theme` (4)                | The chrome rendering with no colour of its own, so the tokens are the only source; **identical markup whether the block is first created light or dark**, which is what criterion 3 reduces to once there is no stylesheet to swap; and a theme flip re-tinting in place without a re-render. The contrast itself is not a spec — it is `check-tokens.mjs`, on every lint run (§7.1)                                                                                                                                                                                  |
| Copying a block                  | `transcript` (10 of 43)         | The exact source on the clipboard, entities and all; the two-second confirmation and its return to "Copy"; every block served from **one** listener; the live region announcing, and announcing **again** for a second copy taken inside the first's window (observed with a `MutationObserver`, because reading the text would pass on the stale value); the accessible name following the visible label per SC 2.5.3; destruction inside the confirmation window; a missing clipboard API and a refused one; and a click that is not a copy control falling through |
| Copying a message                | `message-copy` (10)             | The text it was handed, untouched; the shared window and the return; **the accessible name in step with the visible label**; a prompt and a response named apart; a second copy restarting rather than inheriting the first window; a refused clipboard and an absent one both leaving the control alone; destruction before the clipboard settles scheduling nothing; and that the control is natively focusable, which is what §9.2's reveal depends on                                                                                                             |
| The deferred loaders             | `lazy-renderer-loader` (12)     | The import **never reached** with a flag off; one load for an answer with six diagrams; a failed chunk degrading to `null` and retrying later; `parse` before `render` and never rendering a failed source; the configuration that cannot execute anything; a unique id per diagram; **theme variables dropped rather than passed empty**; KaTeX's options; `errorColor` omitted rather than empty; and the stylesheet linked **once**, from this origin                                                                                                              |
| Drawing and typesetting          | `markdown-extras` (14)          | With the flags off, a mermaid fence rendering as a plain code block with no error and math rendering as its own source; with them on, the diagram drawn **and its source kept**, a failure showing source plus notice, the SVG sanitized before it lands, an ordinary code block left alone, and a theme flip redrawing; and on the math side the four cases that justify the token layer — mid-sentence, across lines, the LaTeX bracket forms, and the escapes marked would otherwise eat — plus prose about money and a `$$` inside a fence both left alone        |
| The diagram profile              | `diagram-svg` (7)               | The elements a diagram is drawn from kept; `script`, event handlers and `foreignObject` stripped; links refused however they are spelled; **CSS that would reach off this origin rewritten, and a local `url(#…)` kept**; and — twice — that the markdown profile is untouched before and after, which is the hook leak this could otherwise cause                                                                                                                                                                                                                    |
| The split                        | `streaming-split` (30)          | Last boundary not the first; the delimiter staying on the head; a trailing blank line held back until the line after it arrives; an unclosed fence overriding the blank-line rule; a blank line inside a fence ignored; tilde fences, and a backtick line refusing to close one; a shorter fence refusing to close a longer one; indented continuations refused; text opening on a blank line returning rather than hanging; **the head only ever growing, asserted across every prefix of several sources**                                                          |
| The two renderers and the footer | `assistant-turn` (14)           | Two renderers while streaming and one once settled; closed blocks in the head and growing text in the tail; the caret on the tail, and on the head when nothing is left over; **only the tail re-parsing until a boundary is crossed**; a still-arriving code block unhighlighted and the same block highlighted at settle; **the settled render character-identical to a single full render**; and the footer's inverted gate — present without counts, absent while streaming, and **no Copy control on a turn that settled empty**                                 |
| Pinning                          | `transcript-pinning` (8)        | The sentinel observed against the container with the 80 px margin; following while at the bottom; holding position and offering the control once scrolled up; the control scrolling, resuming and **handing focus to the transcript rather than dropping it**; no jump when a turn finishes while scrolled up; the same sentinel surviving an empty-state → transcript swap                                                                                                                                                                                           |

Every spec that reads rendered output drives it through the real pipeline with [`src/testing/markdown-host.ts`](../../enterprise-gpt-ui/src/testing/markdown-host.ts), which resolves on `MarkdownComponent.ready` rather than counting microtasks — the renderer writes its output a beat after change detection returns, so reading the DOM early would pass a negative assertion for the wrong reason.

jsdom implements neither observer, and could not usefully: with no layout, nothing ever intersects or resizes. [`src/testing/intersection-observer.ts`](../../enterprise-gpt-ui/src/testing/intersection-observer.ts) and [`resize-observer.ts`](../../enterprise-gpt-ui/src/testing/resize-observer.ts) install controllable fakes from `setup-dom.ts`, and specs drive them with `setIntersecting` / `resizeElement`. The intersection fake deliberately **does not** fire on `observe`: a real observer's first record is asynchronous, so code that is only correct once it arrives is code with a visible wrong state first, and leaving the fake silent keeps that mistake failing.

```bash
# from enterprise-gpt-ui/
npm test        # Vitest, single run — 1099 specs
npm run lint    # ESLint (incl. the bypassSecurityTrustHtml ban) + icon, forbidden-API and token checks
npm run build   # budgets, then check-initial-chunk.mjs — the markdown stack lazy, mermaid/katex lazy and present
```

> **No automated test in this repository runs real Mermaid, and none ever will.** jsdom implements no `getBBox`, no `getComputedTextLength` and no `document.fonts`, which is what Mermaid's layout is built on. That is precisely why the `import()` literals sit behind the `DIAGRAM_MODULE` and `MATH_MODULE` tokens: every spec above substitutes a fake module through a provider override, so what is verified is this application's own behaviour — the flags, the parse-before-render order, the configuration passed, the sanitizing, the fallbacks and the redraws — and _not_ that Mermaid draws a correct picture. **The manual pass with both flags on is therefore load-bearing rather than optional.** It was run against a live API on 2026-08-14 and everything above held: a flowchart drew, a deliberately broken source kept its text and took the notice, the Copy control still returned the source from behind the drawn diagram, a theme toggle redrew it, and all four math forms — `\(…\)`, `\[…\]`, a single-line `$$…$$` and one written across lines — typeset, with `$5 to $10` and a `$$` inside a fence both left alone. Re-run it whenever this pipeline changes; nothing in the suite can stand in for it.

> **Verification status.** As with the epics before it, none of this has been exercised against a live API: the gates above are green and every behaviour here is asserted in Vitest, with the stream driven from recorded fixtures.

## 11. Deliberately not here

Recorded in the PRD and the build order, not omissions:

- **A light Prism stylesheet, and any theme swap at all** — US-604's second criterion, deviated from with the product owner's agreement. The board fixes the code surface dark in both themes, so there is nothing to swap to; the palette is token-driven and gated instead (§7.1).
- **A diagram that appears while the answer streams** — US-605 draws at settle, with nothing planned against it (§8.2).
- **Feedback controls in the message footer** — US-1103, which needs the backend enabler US-1102 first. The footer's 14 px gap and its reveal are already the row they will join (§9.2).
- **The streaming live-region treatment** — US-1402. Today `aria-busy` rides the transcript for the length of a turn, one persistent `role="status"` region announces abnormal endings and a second carries the code-block copy confirmation ([turn lifecycle §8](../conversations/turn-lifecycle.md#8-rendering-the-turn)).
- **A `tabindex` on a scrolling `<pre>`** — US-1401, which owns keyboard operability and the axe gate (§3.5).
- **A `bi-copy` glyph on the code block's control** — nothing planned; admitting `svg`/`use` to the sanitizer profile is a much larger surface than that control is worth (§3.5). The message footer's control has the glyph, because it is a component rather than rendered `innerHTML` (§9.1).

## 12. Troubleshooting

| Symptom                                                                  | Cause                                                                                                                                                                                                                                                                                                                                     |
| ------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| An image in an answer renders as its alt text                            | Deliberate (§1, decision 3, and §3.4). `img` is absent from `CHAT_ALLOWED_TAGS` so the client issues no third-party request; the alt text is the substitute, not a fallback that failed                                                                                                                                                   |
| Ordinary markup vanishes from an answer                                  | It arrived as raw HTML, which layer one drops at the parser. Only markdown syntax renders — that is the boundary, not a bug                                                                                                                                                                                                               |
| A code block is not highlighted mid-stream                               | Expected: `stripFenceInfo` removes the info string from the tail, and highlighting returns with the canonical render at settle (§4.2). If it is missing _after_ settle, check the language is one of the seven grammars `prism.ts` registers                                                                                              |
| The head bar names no language on a block that is still arriving         | Same cause, same expectation: the tail's fences have no info string, so there is nothing to name. The Copy control is offered regardless (§3.5)                                                                                                                                                                                           |
| Copy does nothing, on every block at once                                | The delegated listener is on the `Transcript` host. Check the control still carries `.md-code__copy` and sits inside a `.md-code` wrapper — the listener matches on both, and the block is read from `pre > code` within it (§3.5)                                                                                                        |
| A second copy inside two seconds says nothing to a screen reader         | The `afterNextRender` deferral was removed. Clearing and re-setting the live region in one task leaves the DOM untouched, so no transition is announced (§3.5)                                                                                                                                                                            |
| The caret renders below a streaming code block, inside its box           | The `.md-code` case was dropped from the caret rules in `_markdown.scss`, so the generic last-child rule is drawing it on the wrapper (§7)                                                                                                                                                                                                |
| A `mermaid` fence stays a code block after the turn settles              | In order of likelihood: `features.diagrams` is `false` in the deployed `config.json` (the default); the chunk failed to fetch, which logs _"The diagram renderer could not be loaded."_; or `appMarkdownExtras` came off the settled `<markdown>` in `assistant-turn.html`. While the turn is still streaming this is **expected** (§8.2) |
| A drawn diagram loses its colours after a theme flip, or does not redraw | The source `<pre>` was removed rather than hidden, so there is nothing to draw again — it is the render cache as well as the fallback (§8.1). If it redraws against the _previous_ palette, the `afterRenderEffect` was turned into a plain `effect`, which runs before Angular has updated the DOM (§8.4)                                |
| Math renders as its own source                                           | `features.math` is `false`, or KaTeX's chunk failed (check the console). A **malformed** expression rendering as its source in the `--fail` colour is deliberate — `throwOnError: false`, so one bad expression does not take the answer's others with it (§8.5)                                                                          |
| Math renders as unstyled fragments of letters                            | `public/vendor/katex` is missing from the deployment. It is generated, not committed — run `npm run assets`. The console carries the URL that failed (§8.6)                                                                                                                                                                               |
| An expression written with `\(…\)` renders as `(x^2)`                    | Something moved the capture out of the marked extension and into a DOM pass. marked's inline escape class eats those delimiters before a DOM exists; only a tokenizer sees them (§8.5)                                                                                                                                                    |
| A sentence about prices turns into math                                  | A single-`$` delimiter was added. It is absent on purpose (§8.5)                                                                                                                                                                                                                                                                          |
| The build fails naming `diagrams (US-605)` or `math (US-605)`            | Either a static import reached one of them — from `main.ts` _or_ from the chat chunk, which the second traversal covers — or, if the message is "not in the build at all", the `await import(…)` was deleted and the gate is telling you it now has nothing to check (§8.7)                                                               |
| `npm run lint` fails on `prismjs/themes/`                                | Deliberate. There is one code surface and `_prism.scss` tints it from tokens; change that file, not the ban (§7.1)                                                                                                                                                                                                                        |
| `check:tokens` fails with "CONTRAST_PAIRS does not measure it"           | A new `var(--code-*)` was used in `_prism.scss` or `_markdown.scss`. Add it to the pair list with the minimum it has to clear — the gate is refusing to fall silently behind the stylesheet (§7.1)                                                                                                                                        |
| The message footer never appears                                         | The reveal was replaced by an `@if`, or the `:focus-within` / `:has(.message-copy--copied)` selectors were dropped. Opacity is what keeps the control focusable and the height stable (§9.2)                                                                                                                                              |
| The prompt row jumps ~28 px when a turn settles                          | The empty footer under the optimistic user bubble was removed, so the row grows at settle and the column below it shifts (§9.2)                                                                                                                                                                                                           |
| A settled turn with no text offers a Copy control that "works"           | The text gate came off. `writeText('')` succeeds, so the control confirms having wiped the clipboard (§9.2)                                                                                                                                                                                                                               |
| The whole answer re-renders on every flush                               | The head moved backwards. Something changed a boundary rule in `splitStreamingMarkdown`, or the source arrived with `\r\n` line endings, which match no boundary at all (§4.1)                                                                                                                                                            |
| The transcript lags one flush behind the answer                          | Pinning was moved onto a signal effect. The renderer writes a microtask after change detection returns; only the `ResizeObserver` sees the real geometry (§5.2)                                                                                                                                                                           |
| The jump control flashes on every mount                                  | `_atBottom` no longer starts `true`. A real observer's first record is asynchronous, and "not yet reported" is not "scrolled up" (§5.1)                                                                                                                                                                                                   |
| The jump control never disappears, or the page jumps back                | The sentinel was moved inside the body's `@if` chain, so a body swap replaced it and the observer is watching a detached node (§5.1)                                                                                                                                                                                                      |
| Focus lands on `<body>` after pressing jump                              | The container lost its `tabindex="-1"`, so the directive's `focus()` is a no-op and focus falls through when the control removes itself (§5.3)                                                                                                                                                                                            |
| The build fails naming `ngx-markdown` or `prismjs`                       | Something eagerly reachable from `main.ts` now imports the renderer statically. Move the import behind the lazy route or provide it at the route's component — do not relax the check (§6)                                                                                                                                                |
| Prism highlights nothing in a spec                                       | `prism.ts` must be imported for its value, not only its side effects: the specs' global object is not the one Prism writes itself to, and the file assigns `globalThis.Prism` explicitly for that reason                                                                                                                                  |
