import { MarkedExtension, Tokens } from 'marked';

/** Marks a block whose text is a LaTeX expression waiting to be typeset (US-605). */
export const MATH_CLASS = 'md-math';

/** Distinguishes `$$…$$` and `\[…\]` from `\(…\)` once the source is in the DOM. */
export const MATH_DISPLAY_CLASS = 'md-math--display';

const DISPLAY_BLOCK = /^ {0,3}(?:\$\$|\\\[)([\s\S]+?)(?:\$\$|\\\])[ \t]*(?:\n|$)/;
/**
 * Line-anchored, and that is load-bearing rather than tidy. `start` tells marked
 * where a block of this kind *may* begin, and marked cuts the current paragraph
 * there — so an unanchored match would split "an expression: $$x^2$$ inline" into
 * three lines at the first `$$`, which `breaks: true` then joins with `<br>`. The
 * block tokenizer can only ever match at a line start, so `start` must agree.
 */
const DISPLAY_BLOCK_START = /^ {0,3}(?:\$\$|\\\[)/m;

const DISPLAY_SPAN = /^\$\$([^\n]+?)\$\$|^\\\[([^\n]+?)\\\]/;
const INLINE_SPAN = /^\\\(([\s\S]+?)\\\)/;
const SPAN_START = /\$\$|\\[([]/;

function escapeMath(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/**
 * Captures LaTeX at the **token** layer, before marked can rewrite it (US-605).
 *
 * A DOM pass over rendered output — `katex/contrib/auto-render`, which is what
 * `ngx-markdown`'s own `katex` integration runs — cannot work here, and three
 * separate things in this pipeline are why:
 *
 * 1. **`\(`, `\)`, `\[` and `\]` are in marked's inline escape class**, so
 *    `\(x^2\)` reaches the DOM as `(x^2)`. Delimiters configured for those forms
 *    match nothing, ever.
 * 2. **`breaks: true` puts a `<br>` between the markers** of a `$$…$$` opened and
 *    closed on separate lines — the commonest display form there is — and
 *    auto-render only joins *consecutive text nodes*, so an element between them
 *    ends the run and the expression is never seen.
 * 3. **marked strips LaTeX's own escapes**: `\{`, `\}`, `\_`, `\%`, `\&`, `\#` and
 *    the `\\` row separator all vanish, so sets, `aligned` bodies and escaped
 *    subscripts render *wrong* rather than not at all — which is worse, because
 *    nothing reports it.
 *
 * A tokenizer runs before every one of those rules and takes the source verbatim,
 * which is the only place the expression still exists as the model wrote it. What
 * it emits is an escaped placeholder that `MarkdownExtras` typesets on demand, so
 * KaTeX stays out of the bundle until an expression actually appears.
 *
 * There is deliberately **no single-`$` form**. "it costs $5 to $10" is the classic
 * false positive, and prose is far commoner in an answer than inline math.
 */
export function mathExtension(): MarkedExtension {
  return {
    extensions: [
      {
        name: 'mathBlock',
        level: 'block',
        start: (src: string) => src.match(DISPLAY_BLOCK_START)?.index,
        tokenizer(src: string): Tokens.Generic | undefined {
          const match = DISPLAY_BLOCK.exec(src);

          return match === null
            ? undefined
            : { type: 'mathBlock', raw: match[0], text: (match[1] ?? '').trim() };
        },
        renderer: (token: Tokens.Generic) =>
          `<div class="${MATH_CLASS} ${MATH_DISPLAY_CLASS}">` +
          `${escapeMath(String(token['text']))}</div>\n`,
      },
      {
        name: 'mathInline',
        level: 'inline',
        start: (src: string) => src.match(SPAN_START)?.index,
        tokenizer(src: string): Tokens.Generic | undefined {
          // `$$` and `\[` are display math wherever they appear, `\(` is not —
          // KaTeX's own auto-render defaults, and what a reader writing either
          // form means by it.
          const display = DISPLAY_SPAN.exec(src);
          const match = display ?? INLINE_SPAN.exec(src);

          return match === null
            ? undefined
            : {
                type: 'mathInline',
                raw: match[0],
                text: (match[1] ?? match[2] ?? '').trim(),
                display: display !== null,
              };
        },
        // A `code`, because the profile allows it and it is phrasing content: a
        // `div` here would be reparented out of its paragraph by the parser, and
        // KaTeX's display output is a `span` either way.
        renderer: (token: Tokens.Generic) =>
          `<code class="${MATH_CLASS}${token['display'] === true ? ` ${MATH_DISPLAY_CLASS}` : ''}">` +
          `${escapeMath(String(token['text']))}</code>`,
      },
    ],
  };
}
