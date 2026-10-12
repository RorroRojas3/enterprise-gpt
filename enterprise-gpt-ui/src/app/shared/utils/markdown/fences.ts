/** A line-start fence: CommonMark allows up to three spaces of indent and either marker. */
const FENCE = /^ {0,3}(`{3,}|~{3,})([^\n]*)$/gm;

export interface Fence {
  readonly start: number;
  readonly marker: string;
  /** The info string, trimmed. Empty for a bare fence, and always empty on a closer. */
  readonly info: string;
}

/** A fence pair and the span it covers, half-open `[start, end)`. */
export interface ClosedFence {
  readonly start: number;
  readonly end: number;
  readonly info: string;
  /** CommonMark requires an empty closer; anything else means a nested opener closed it. */
  readonly closerInfo: string;
  /** Half-open span of the content between the two fence lines. */
  readonly contentStart: number;
  readonly contentEnd: number;
}

/** Closed blocks in source order, and the open one if the text ends inside a fence. */
export interface FenceScan {
  readonly closed: readonly ClosedFence[];
  readonly open: Fence | null;
}

/**
 * A block closes only on a later fence of the same character, at least as long, so a
 * four-backtick block can quote a three-backtick one.
 */
export function scanFences(text: string): FenceScan {
  const closed: ClosedFence[] = [];
  let open: Fence | null = null;
  let contentStart = 0;

  for (const match of text.matchAll(FENCE)) {
    const marker = match[1] ?? '';
    const start = match.index;

    if (open === null) {
      open = { start, marker, info: (match[2] ?? '').trim() };
      const openerEnd = text.indexOf('\n', start);
      contentStart = openerEnd === -1 ? text.length : openerEnd + 1;
      continue;
    }

    const closes = marker[0] === open.marker[0] && marker.length >= open.marker.length;
    if (closes) {
      const lineEnd = text.indexOf('\n', start);
      closed.push({
        start: open.start,
        end: lineEnd === -1 ? text.length : lineEnd,
        info: open.info,
        closerInfo: (match[2] ?? '').trim(),
        contentStart,
        contentEnd: start,
      });
      open = null;
    }
  }

  return { closed, open };
}
