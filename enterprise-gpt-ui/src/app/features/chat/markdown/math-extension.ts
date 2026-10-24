import { MarkedExtension, Tokens } from 'marked';

/** Marks a block whose text is a LaTeX expression waiting to be typeset. */
export const MATH_CLASS = 'md-math';

/** Distinguishes `$$…$$` and `\[…\]` from `\(…\)` once the source is in the DOM. */
export const MATH_DISPLAY_CLASS = 'md-math--display';

const DISPLAY_BLOCK = /^ {0,3}(?:\$\$|\\\[)([\s\S]+?)(?:\$\$|\\\])[ \t]*(?:\n|$)/;
/**
 * Line-anchored: marked cuts the paragraph wherever `start` matches, so an inline `$$` would
 * split it, and the block tokenizer only matches at a line start anyway.
 */
const DISPLAY_BLOCK_START = /^ {0,3}(?:\$\$|\\\[)/m;

const DISPLAY_SPAN = /^\$\$([^\n]+?)\$\$|^\\\[([^\n]+?)\\\]/;
const INLINE_SPAN = /^\\\(([\s\S]+?)\\\)/;
const SPAN_START = /\$\$|\\[([]/;

function escapeMath(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/**
 * Captures LaTeX as tokens, not in the DOM: by then marked has eaten `\(`/`\[` and LaTeX's own
 * escapes, and `breaks: true` has split multi-line `$$`. No single-`$` form: "it costs $5 to
 * $10" is the classic false positive.
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
          // `$$` and `\[` are display math anywhere and `\(` is not, as in KaTeX auto-render.
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
        // A `code`: allowed phrasing content, where a `div` would be moved out of its paragraph.
        renderer: (token: Tokens.Generic) =>
          `<code class="${MATH_CLASS}${token['display'] === true ? ` ${MATH_DISPLAY_CLASS}` : ''}">` +
          `${escapeMath(String(token['text']))}</code>`,
      },
    ],
  };
}
