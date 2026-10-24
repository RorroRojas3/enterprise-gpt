import { scanFences } from '@features/chat/markdown/fences';
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
 * Comma- or semicolon-separated; `Alice <a@b.com>` reduces to the address, since a display name
 * rarely survives a `mailto:` recipient.
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
 * Headers end at the first non-header line, blank included, so a body line such as "To: confirm
 * the date…" is never mistaken for one and dropped.
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
// `[^\S\n]`, not `[ \t]`: it must match every space `String.trim` strips off the info string,
// or this rejects a block the scanner would accept.
const ANY_EMAIL_FENCE = /^ {0,3}(?:`{3,}|~{3,})[^\S\n]*email[^\S\n]*$/im;

/**
 * Only a closed fence yields a card: a streaming one stays a code block rather than a half card,
 * as does one closed by a nested opener, whose body would be truncated.
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
 * Detects an unfenced email by two independent markers, since advice about email quotes subject
 * lines too. Recipients come only from header lines, never from prose a document could plant.
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
