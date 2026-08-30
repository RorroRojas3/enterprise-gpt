import { scanFences } from './fences';

/**
 * A streaming answer cut into the part that has stopped changing and the part
 * that is still arriving (US-602).
 *
 * `head` is always a prefix of the source and only ever grows, which is the whole
 * point: rendering it produces an identical string until a new block boundary is
 * crossed, so the renderer bound to it re-parses on block boundaries rather than
 * on every token. Every rule below is written to preserve that — a boundary that
 * could move backwards as more text arrives would re-parse the whole answer on
 * the flush that moved it.
 *
 * Offsets are byte-for-byte into the source, so `head + tail === text` always.
 * That also means the source must use `\n` line endings, which the SSE codec
 * guarantees; `\r\n` would simply never match a boundary and render as one block.
 */
export interface StreamSplit {
  readonly head: string;
  readonly tail: string;
}

const EMPTY_SPLIT: StreamSplit = { head: '', tail: '' };

/**
 * Splits accumulated answer text at the last point where one block ends and the
 * next begins, so a streaming turn can render a stable head and a volatile tail
 * as two separate renderers (US-602).
 *
 * Three things start a block, and the split takes whichever comes last:
 *
 * - a blank line, the ordinary case;
 * - the opener of a fence that has not closed yet — a head cut off mid-fence
 *   would parse as an unterminated code block and a tail starting mid-fence
 *   would not parse as code at all, so everything from the opener stays together;
 * - the line after a fence that *has* closed, which is what keeps the head from
 *   collapsing when a fence had no blank line before it. Without this the
 *   boundary would jump backwards the moment a block closed, and on an answer
 *   with no blank lines at all the head would fall to nothing and stay there.
 *
 * A blank line inside a fence is content rather than a boundary, and a boundary
 * whose next line is indented is refused: the two renderers are parsed
 * separately, and marked re-bases the indentation of whatever it is given, so an
 * indented tail would come out as a paragraph where the whole source would have
 * given an indented code block. A boundary at the very end of the text is
 * refused on the same rule, because the line after it is not yet known — which
 * is why a text ending in a blank line keeps that blank line in the tail for one
 * more delta rather than committing to a boundary it may have to take back.
 *
 * The delimiter stays with the head.
 */
export function splitStreamingMarkdown(text: string): StreamSplit {
  if (text === '') {
    return EMPTY_SPLIT;
  }

  const { closed, open } = scanFences(text);

  // Inside an open fence nothing later can be a boundary, so the opener is the
  // answer outright and no blank line after it may be considered.
  if (open !== null) {
    return open.start <= 0
      ? { head: '', tail: text }
      : { head: text.slice(0, open.start), tail: text.slice(open.start) };
  }

  const inFence = (offset: number): boolean =>
    closed.some(({ start, end }) => offset >= start && offset < end);

  // A boundary whose next line has not arrived yet counts as indented. The
  // verdict has to be one-way — refused until proven otherwise — or a boundary
  // accepted while the following line was still empty would be withdrawn the
  // moment a space arrived, and the head would shrink.
  const indented = (offset: number): boolean =>
    offset + 2 >= text.length || /^[ \t]/.test(text.slice(offset + 2));

  // `boundary > 0`, not `!== -1`: `lastIndexOf` clamps a negative start to 0, so
  // searching back from index 0 returns 0 again and the loop never ends.
  let boundary = text.lastIndexOf('\n\n');
  while (boundary > 0 && (inFence(boundary) || indented(boundary))) {
    boundary = text.lastIndexOf('\n\n', boundary - 1);
  }
  if (boundary === 0 && (inFence(0) || indented(0))) {
    boundary = -1;
  }

  const afterBlankLine = boundary === -1 ? 0 : boundary + 2;
  const lastClosed = closed[closed.length - 1];
  const afterFence = lastClosed === undefined ? 0 : Math.min(lastClosed.end + 1, text.length);
  const split = Math.max(afterBlankLine, afterFence);

  return split <= 0
    ? { head: '', tail: text }
    : { head: text.slice(0, split), tail: text.slice(split) };
}

/**
 * Removes the info string from every line-start fence.
 *
 * This is how the tail opts out of syntax highlighting while still going through
 * the one renderer: without a `language-*` class the highlighter has no grammar
 * to apply and leaves the block alone. Mid-stream that is the honest rendering —
 * the fence may be half a line of code — and the canonical render at settle puts
 * the language back by rendering the untouched source.
 */
export function stripFenceInfo(markdown: string): string {
  return markdown.replace(/^( {0,3}(?:`{3,}|~{3,}))[^\n]*$/gm, '$1');
}
