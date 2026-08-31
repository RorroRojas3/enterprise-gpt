# Answer Rendering

Turning model output into DOM: the markdown pipeline, its sanitizer, the streaming split, and the
two libraries loaded only when an answer needs them.

## Using it

The providers are installed on the chat route, so a component under it binds source text:

```html
<markdown class="assistant-turn__md" [data]="node.text" />
```

The `assistant-turn__md` class is what the global stylesheet targets — without it the output renders
unstyled, because rendered markup is `innerHTML` and out of reach of component styles.

For a **settled** answer that may contain a diagram or an expression, add the directive. It does
nothing until the content is there and the flag is on, so there is no condition to write:

```html
<markdown class="assistant-turn__md" appMarkdownExtras [data]="node.text" />
```

A surface that is not chat provides the same factory on its own route component, never in
`app.config.ts`:

```ts
@Component({
  providers: [ConversationStore, TurnStore, provideChatMarkdown()],
})
export class Chat {}
```

## Five rules the build enforces

- **Never import `ngx-markdown`, `marked`, `prismjs` or `dompurify` from code reachable by a static
  import from `main.ts`.** `npm run build` fails naming the library and the chunk.
- **Never import `mermaid` or `katex` statically at all** — not from `main.ts`, and not from the chat
  chunk. Both are reached through DI tokens, and the gate fails the build from either root.
- **Never call `bypassSecurityTrustHtml`.** `npm run lint` fails on the syntax, and the renderer
  already performs the trust behind its own sanitizer.
- **Never widen the sanitizer profile in place.** `sanitizeChatMarkdown` passes its configuration per
  call rather than through `DOMPurify.setConfig`, so no other caller can inherit or widen it. A
  surface needing different allowances gets its own function.
- **`appMarkdownExtras` goes on the settled render only**, never on the streaming pair.

## Two layers between model output and the DOM

**Layer one — raw HTML never becomes markup.** A `renderer.html = () => ''` override drops raw HTML
at the parser, so it never reaches the sanitizer at all.

**Layer two — DOMPurify with a closed profile.** Supplying a `SANITIZE` function to `ngx-markdown`
*replaces* Angular's `DomSanitizer` in that pipeline, so DOMPurify's closed profile is the only
DOM-boundary layer. That is why layer one exists: with Angular's sanitizer out of the path, a single
misconfiguration would be the whole defence.

`ngx-markdown` performs the `bypassSecurityTrustHtml` internally, which is why application code has
zero call sites for it.

## Streaming: a stable head and a volatile tail

A partially arrived markdown buffer cannot simply be handed to the parser — an unterminated fence or
emphasis would render as garbage and then re-render differently on the next delta.
`domain/markdown/streaming-split.ts` splits the buffer at the last position where the markdown is
provably complete:

- The **head** is parsed and rendered as markdown.
- The **tail** is rendered as plain text, and is **not** syntax-highlighted — highlighting a fragment
  costs work that the next delta invalidates.
- On settle, the whole buffer is re-rendered once as markdown.

The pair is wrapped so the blinking caret can sit at the end of the tail without being inside the
markdown output.

## Following the newest content

Following is a `ResizeObserver`, not a signal effect: content grows for reasons no signal reports
(an image loading, a diagram rendering, a font swapping), and only the observer sees those.

Bottom detection has a tolerance band rather than an exact comparison, because sub-pixel layout
means the scroll position rarely equals the exact bottom. A jump control appears when the reader has
scrolled away, and following resumes when they return to the bottom.

## Diagrams and math

`mermaid` and `katex` are dependencies, and are in **neither** bundle graph — roughly 1.5 MB and
280 kB respectively, in lazy chunks of their own, reached only through `await import(...)` behind DI
tokens and behind `config.json` flags. They are fetched only when flagged content actually appears.

- **The renderer's whole involvement is one class**, applied by the directive to a rendered block.
- **It runs on the settled render, never on the stream.** A diagram source is invalid for most of its
  arrival, so rendering it per delta would mean a stream of parse errors and thrown-away work.
- **Two sanitizer profiles stay apart.** A diagram's output legitimately needs SVG elements the chat
  profile refuses, so it has its own function rather than a widened shared one.
- **Mermaid is configured to draw, not to run** — its click and script directives are off.

`npm run assets:katex` fills the gitignored `public/vendor/katex` with KaTeX's CSS and fonts, which
are injected as a `<link>` when the first expression appears rather than bundled into the styles
chunk.

`check-initial-chunk.mjs` asserts both libraries are present *somewhere*, because every other check
in it passes when a library is simply gone.

## Styling output the templates never see

Rendered markdown is `innerHTML`, so component styles cannot reach it. `_markdown.scss` and
`_prism.scss` are global and token-driven; no Prism stylesheet is imported, and `check:forbidden`
refuses one.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/app/features/chat/markdown/` | The provider factory, the extras directive, the lazy loader |
| `enterprise-gpt-ui/src/app/domain/markdown/streaming-split.ts` | Head/tail split |
| `enterprise-gpt-ui/src/app/domain/markdown/fences.ts` | Fence scanning, shared with the email contract |
| `enterprise-gpt-ui/src/styles/_markdown.scss` | Styling for output out of reach of components |
| `enterprise-gpt-ui/src/styles/_prism.scss` | Token-driven highlighting |
| `enterprise-gpt-ui/scripts/check-initial-chunk.mjs` | The must-stay-lazy gate |
| `enterprise-gpt-ui/src/app/features/chat/markdown/code-theme.spec.ts` | Code surface legibility |

## Related

- [design-system.md](design-system.md)
- [../conversations/streaming.md](../conversations/streaming.md)
- [../conversations/export.md](../conversations/export.md)
