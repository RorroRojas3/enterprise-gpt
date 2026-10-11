import { scanFences } from './fences';

/**
 * `head` only ever grows, so its renderer re-parses on block boundaries rather than on every
 * token. `head + tail === text`, which needs the `\n` line endings the SSE codec guarantees.
 */
export interface StreamSplit {
  readonly head: string;
  readonly tail: string;
}

const EMPTY_SPLIT: StreamSplit = { head: '', tail: '' };

/**
 * Splits at the last block start: a blank line, an unclosed fence's opener, or the line after
 * a closed fence. Refuses a boundary inside a fence or before an indented or unarrived line,
 * since marked re-bases each half's indentation separately. The delimiter stays with the head.
 */
export function splitStreamingMarkdown(text: string): StreamSplit {
  if (text === '') {
    return EMPTY_SPLIT;
  }

  const { closed, open } = scanFences(text);

  // Inside an open fence nothing later can be a boundary, so the opener is the answer.
  if (open !== null) {
    return open.start <= 0
      ? { head: '', tail: text }
      : { head: text.slice(0, open.start), tail: text.slice(open.start) };
  }

  const inFence = (offset: number): boolean =>
    closed.some(({ start, end }) => offset >= start && offset < end);

  // A boundary whose next line has not arrived counts as indented, so a later space can
  // never withdraw it and shrink the head.
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
 * Lets the tail skip syntax highlighting while a fence may hold half a line of code; the
 * settled render of the untouched source restores the language.
 */
export function stripFenceInfo(markdown: string): string {
  return markdown.replace(/^( {0,3}(?:`{3,}|~{3,}))[^\n]*$/gm, '$1');
}
