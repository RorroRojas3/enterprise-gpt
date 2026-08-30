import { scanFences } from '../markdown/fences';
import { markdownToPlainText } from './plain-text';

/** An email the assistant composed, ready to hand to a mail client. */
export interface EmailDraft {
  readonly to: readonly string[];
  readonly cc: readonly string[];
  readonly subject: string | null;
  readonly body: string;
}

/** One run of an answer: ordinary markdown, or an email to render as a card. */
export type EmailSegment =
  | { readonly kind: 'markdown'; readonly start: number; readonly text: string }
  | { readonly kind: 'email'; readonly start: number; readonly draft: EmailDraft };

/** The info string the system prompt asks the model to mark a composed email with. */
const EMAIL_INFO = 'email';

const HEADER = /^(to|cc|subject)\s*:\s*(.*)$/i;

/**
 * An address list is comma- or semicolon-separated, and the display-name form
 * `Alice <a@b.com>` is reduced to the address: a mail client re-resolves the
 * name, and passing the whole thing through a `mailto:` recipient rarely
 * survives the round trip.
 */
function parseAddresses(value: string): readonly string[] {
  return value
    .split(/[,;]/)
    .map((part) => {
      const angled = /<([^>]+)>/.exec(part);
      return (angled?.[1] ?? part).trim();
    })
    .filter((address) => address.length > 0);
}

/**
 * Reads the leading header run off an email's lines.
 *
 * Headers run until the first line that is not one, a blank line included. The
 * run has to end structurally rather than after a fixed number of lines: a body
 * sentence beginning "To: confirm the date…" is body, and dropping it because it
 * matched a header pattern would silently cut a line out of what the user sends.
 */
function readHeaders(lines: readonly string[]): { draft: Omit<EmailDraft, 'body'>; end: number } {
  const to: string[] = [];
  const cc: string[] = [];
  let subject: string | null = null;
  let index = 0;

  for (; index < lines.length; index++) {
    const line = (lines[index] ?? '').trim();
    if (line === '') {
      index++;
      break;
    }

    const header = HEADER.exec(line);
    if (header === null) {
      break;
    }

    const value = (header[2] ?? '').trim();
    switch ((header[1] ?? '').toLowerCase()) {
      case 'to':
        to.push(...parseAddresses(value));
        break;
      case 'cc':
        cc.push(...parseAddresses(value));
        break;
      default:
        subject = value === '' ? null : value;
    }
  }

  return { draft: { to, cc, subject }, end: index };
}

function parseEmailBlock(content: string): EmailDraft {
  const lines = content.split('\n');
  const { draft, end } = readHeaders(lines);

  return { ...draft, body: markdownToPlainText(lines.slice(end).join('\n')) };
}

/** Cheap rejection for the overwhelmingly common answer that marks no email. */
// `[^\S\n]` rather than `[ \t]`, to match every space `String.trim` would strip
// off the info string below — the two must agree or this rejects a block the
// scanner would have accepted.
const ANY_EMAIL_FENCE = /^ {0,3}(?:`{3,}|~{3,})[^\S\n]*email[^\S\n]*$/im;

/**
 * Cuts an answer into the email blocks the model marked and the markdown around
 * them, preserving source order and offsets.
 *
 * Only a **closed** fence yields a card. A fence still being streamed is left as
 * markdown, so a half-written email renders as the code block it currently is
 * rather than flickering through a card with half a body in it — and so is one
 * closed by a nested opener, whose card would carry a body cut off at the nested
 * fence and hand that truncation to a mail client.
 */
export function splitEmailSegments(text: string): readonly EmailSegment[] {
  const whole: readonly EmailSegment[] = [{ kind: 'markdown', start: 0, text }];

  // Runs on every streaming flush, so the answer that has no email at all pays
  // one regex rather than a full fence scan and the array building behind it.
  if (!ANY_EMAIL_FENCE.test(text)) {
    return whole;
  }

  const { closed } = scanFences(text);
  const blocks = closed.filter(
    (fence) => fence.info.toLowerCase() === EMAIL_INFO && fence.closerInfo === '',
  );

  if (blocks.length === 0) {
    return whole;
  }

  const segments: EmailSegment[] = [];
  let cursor = 0;

  for (const block of blocks) {
    if (block.start > cursor) {
      segments.push({ kind: 'markdown', start: cursor, text: text.slice(cursor, block.start) });
    }

    segments.push({
      kind: 'email',
      start: block.start,
      draft: parseEmailBlock(text.slice(block.contentStart, block.contentEnd)),
    });
    cursor = Math.min(block.end + 1, text.length);
  }

  if (cursor < text.length) {
    segments.push({ kind: 'markdown', start: cursor, text: text.slice(cursor) });
  }

  return segments;
}

const GREETING = /^(hi|hello|hey|dear|good (morning|afternoon|evening))\b[^\n]*[,:]\s*$/i;
const SIGN_OFF =
  /^(best|best regards|kind regards|warm regards|regards|sincerely|sincerely yours|yours sincerely|yours truly|thanks|thank you|many thanks|cheers|all the best|take care)[,.!]?\s*$/i;
const HEADING = /^ {0,3}#{1,6}\s+\S/;

/**
 * Recognises an email the model wrote without marking it, for models that ignore
 * the fence and for answers written before it was asked for.
 *
 * Two independent markers are always required, never one: an answer *about* email
 * quotes a subject line as readily as one that is an email, so a lone `Subject:`
 * decides nothing. It never harvests a recipient out of prose either — an address
 * becomes a `To:` only on a header line, or a document the model was fed could
 * choose who the user's mail client addresses.
 */
export function detectEmailDraft(markdown: string): EmailDraft | null {
  const text = markdown.trim();
  if (text === '') {
    return null;
  }

  const lines = text.split('\n');

  // A fenced or heavily headed answer is a document about something, not a
  // message to send.
  if (scanFences(text).closed.length > 0 || lines.filter((l) => HEADING.test(l)).length > 2) {
    return null;
  }

  const { draft, end } = readHeaders(lines);
  const body = lines.slice(end);
  const hasGreeting = body.some((line) => GREETING.test(line.trim()));
  const hasSignOff = body.some((line) => SIGN_OFF.test(line.trim()));
  const markers = [draft.subject !== null, hasGreeting, hasSignOff].filter(Boolean).length;

  return markers < 2 ? null : { ...draft, body: markdownToPlainText(body.join('\n')) };
}
