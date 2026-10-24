import { EmailDraft } from './email-draft';

/**
 * Below Outlook's ~2000 characters, past which it silently truncates the body; a longer draft is
 * copied for the user to paste instead.
 */
export const MAILTO_SAFE_LENGTH = 1800;

/** Compose deep link for Outlook on the web, for a user with no desktop client. */
const OUTLOOK_WEB_COMPOSE = 'https://outlook.office.com/mail/deeplink/compose';

/** Punctuation with an exact ASCII spelling; letters stay out, so `café` never becomes `cafe`. */
const ASCII_PUNCTUATION: readonly (readonly [RegExp, string])[] = [
  [/[\u2018\u2019\u201a\u201b\u2032]/g, "'"],
  [/[\u201c\u201d\u201e\u201f\u2033]/g, '"'],
  // One hyphen for every dash: `--` reads as a typo in a message someone is about to send.
  [/[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]/g, '-'],
  [/\u2026/g, '...'],
  [/\u2022/g, '-'],
  [/[\u00a0\u2002\u2003\u2007\u2009\u200a\u202f\u205f\u3000]/g, ' '],
  // Alternation, not a character class: a zero-width joiner inside one is exactly
  // what `no-misleading-character-class` exists to catch.
  [/\u200b|\u200c|\u200d|\u2060|\ufeff/g, ''],
];

/**
 * `mailto:` escapes are UTF-8 (RFC 6068) but Outlook decodes them in the ANSI code page, so `’`
 * arrives as `â€™`; ASCII survives every code page. Only the `mailto:` handoff needs this.
 */
export function toMailSafeText(text: string): string {
  return ASCII_PUNCTUATION.reduce(
    (folded, [pattern, replacement]) => folded.replace(pattern, replacement),
    text,
  );
}

/** Mail bodies are CRLF-delimited; a client given bare `\n` can run the email onto one line. */
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
  // Recipients go in the path, as RFC 6068 specifies; `@` stays literal there, since `%40` trips
  // older clients.
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
