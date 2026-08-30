import { EmailDraft } from './email-draft';

/**
 * The longest `mailto:` worth handing to the platform.
 *
 * There is no specified limit; the binding one is the receiving client. Outlook
 * stops at roughly 2000 characters and truncates the body silently rather than
 * refusing, so the ceiling is set below that and the caller copies the full text
 * whenever it is crossed. A draft that is quietly missing its last paragraph is
 * worse than one the user was told to paste.
 */
export const MAILTO_SAFE_LENGTH = 1800;

/** Compose deep link for Outlook on the web, for a user with no desktop client. */
const OUTLOOK_WEB_COMPOSE = 'https://outlook.office.com/mail/deeplink/compose';

/**
 * Typographic characters a model emits constantly, and the ASCII they mean.
 *
 * Every one is a punctuation mark with an exact plain-text spelling, so folding
 * it changes how the email looks and not what it says. Letters are deliberately
 * absent: `café` must not become `cafe`.
 */
const ASCII_PUNCTUATION: readonly (readonly [RegExp, string])[] = [
  [/[\u2018\u2019\u201a\u201b\u2032]/g, "'"],
  [/[\u201c\u201d\u201e\u201f\u2033]/g, '"'],
  // One hyphen for every dash, the em dash included. `--` is the older typewriter
  // spelling and reads as a typo in a message someone is about to send.
  [/[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]/g, '-'],
  [/\u2026/g, '...'],
  [/\u2022/g, '-'],
  [/[\u00a0\u2002\u2003\u2007\u2009\u200a\u202f\u205f\u3000]/g, ' '],
  // Alternation, not a character class: a zero-width joiner inside one is exactly
  // what `no-misleading-character-class` exists to catch.
  [/\u200b|\u200c|\u200d|\u2060|\ufeff/g, ''],
];

/**
 * Rewrites the typography a mail client cannot be trusted to decode.
 *
 * `mailto:` percent-escapes are UTF-8 by RFC 6068, and Outlook reads them in the
 * system ANSI code page instead — so a curly apostrophe arrives as `â€™`, which
 * is what a reader sees in the draft they are about to send. No encoding is both
 * correct and safe here, because the receiving client is the thing that is wrong,
 * so the body is spelled in the ASCII every code page agrees on.
 *
 * Only the `mailto:` handoff needs this. The card, the clipboard and the webmail
 * links all carry the model's own typography intact.
 */
export function toMailSafeText(text: string): string {
  return ASCII_PUNCTUATION.reduce(
    (folded, [pattern, replacement]) => folded.replace(pattern, replacement),
    text,
  );
}

/**
 * Mail bodies are CRLF-delimited, and a client that gets bare `\n` can run the
 * whole email onto one line.
 */
function encodeBody(body: string): string {
  return encodeURIComponent(body.replace(/\r\n|\r|\n/g, '\r\n'));
}

function query(draft: EmailDraft, subjectKey: string, fold: (text: string) => string): string {
  const parts: string[] = [];

  if (draft.cc.length > 0) {
    parts.push(`cc=${encodeURIComponent(draft.cc.join(','))}`);
  }
  if (draft.subject !== null) {
    parts.push(`${subjectKey}=${encodeURIComponent(fold(draft.subject))}`);
  }
  if (draft.body !== '') {
    parts.push(`body=${encodeBody(fold(draft.body))}`);
  }

  return parts.join('&');
}

/** The draft as a `mailto:`, for whichever client the OS has registered. */
export function buildMailtoUrl(draft: EmailDraft): string {
  // Recipients sit in the path rather than a `to=` parameter: that is the form
  // RFC 6068 specifies and the one every client accepts. `@` is left literal
  // there — legal per the grammar, and `%40` trips older clients.
  const recipients = draft.to
    .map((address) => encodeURIComponent(address).replace(/%40/g, '@'))
    .join(',');
  const search = query(draft, 'subject', toMailSafeText);

  return `mailto:${recipients}${search === '' ? '' : `?${search}`}`;
}

/** The draft as an Outlook on the web compose link. */
export function buildOutlookWebComposeUrl(draft: EmailDraft): string {
  // No folding: a browser hands this over as UTF-8 and Outlook on the web decodes
  // it as UTF-8, so the model's own typography survives the round trip.
  const search = query(draft, 'subject', (text) => text);
  const to = draft.to.length === 0 ? '' : `to=${encodeURIComponent(draft.to.join(','))}`;
  const parts = [to, search].filter((part) => part !== '');

  return `${OUTLOOK_WEB_COMPOSE}${parts.length === 0 ? '' : `?${parts.join('&')}`}`;
}

/** Whether a built URL is long enough that the client may truncate it. */
export function exceedsMailtoLimit(url: string): boolean {
  return url.length > MAILTO_SAFE_LENGTH;
}

/** The draft as pasteable text, for the clipboard fallback and the Copy action. */
export function formatEmailForClipboard(draft: EmailDraft): string {
  const headers: string[] = [];

  if (draft.to.length > 0) {
    headers.push(`To: ${draft.to.join(', ')}`);
  }
  if (draft.cc.length > 0) {
    headers.push(`Cc: ${draft.cc.join(', ')}`);
  }
  if (draft.subject !== null) {
    headers.push(`Subject: ${draft.subject}`);
  }

  return headers.length === 0 ? draft.body : `${headers.join('\n')}\n\n${draft.body}`;
}
