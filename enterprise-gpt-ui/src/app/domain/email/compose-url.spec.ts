import { describe, expect, it } from 'vitest';
import {
  MAILTO_SAFE_LENGTH,
  buildMailtoUrl,
  buildOutlookWebComposeUrl,
  exceedsMailtoLimit,
  formatEmailForClipboard,
  toMailSafeText,
} from './compose-url';
import { EmailDraft } from './email-draft';

const draft = (over: Partial<EmailDraft> = {}): EmailDraft => ({
  to: [],
  cc: [],
  subject: null,
  body: '',
  ...over,
});

describe('buildMailtoUrl', () => {
  it('puts recipients in the path, as RFC 6068 specifies', () => {
    expect(buildMailtoUrl(draft({ to: ['a@b.com', 'c@d.com'] }))).toBe('mailto:a@b.com,c@d.com');
  });

  it('omits the query entirely when there is nothing to carry', () => {
    expect(buildMailtoUrl(draft())).toBe('mailto:');
  });

  it('encodes the subject, including characters that would split the query', () => {
    expect(buildMailtoUrl(draft({ subject: 'Q3 & Q4' }))).toBe('mailto:?subject=Q3%20%26%20Q4');
  });

  it('sends CRLF line breaks, which is what mail clients expect', () => {
    expect(buildMailtoUrl(draft({ body: 'one\ntwo' }))).toBe('mailto:?body=one%0D%0Atwo');
  });

  it('does not double-encode a body that already uses CRLF', () => {
    expect(buildMailtoUrl(draft({ body: 'one\r\ntwo' }))).toBe('mailto:?body=one%0D%0Atwo');
  });

  it('carries cc alongside the recipients', () => {
    expect(buildMailtoUrl(draft({ to: ['a@b.com'], cc: ['c@d.com'] }))).toBe(
      'mailto:a@b.com?cc=c%40d.com',
    );
  });
});

describe('buildOutlookWebComposeUrl', () => {
  it('passes recipients as a query parameter rather than a path', () => {
    expect(buildOutlookWebComposeUrl(draft({ to: ['a@b.com'], subject: 'Hi' }))).toBe(
      'https://outlook.office.com/mail/deeplink/compose?to=a%40b.com&subject=Hi',
    );
  });

  it('falls back to a bare compose link for an empty draft', () => {
    expect(buildOutlookWebComposeUrl(draft())).toBe(
      'https://outlook.office.com/mail/deeplink/compose',
    );
  });
});

describe('exceedsMailtoLimit', () => {
  it('accepts a url at the ceiling and rejects one past it', () => {
    expect(exceedsMailtoLimit('x'.repeat(MAILTO_SAFE_LENGTH))).toBe(false);
    expect(exceedsMailtoLimit('x'.repeat(MAILTO_SAFE_LENGTH + 1))).toBe(true);
  });

  it('trips on a long body, measuring the encoded url rather than the source', () => {
    expect(exceedsMailtoLimit(buildMailtoUrl(draft({ body: 'a\n'.repeat(700) })))).toBe(true);
  });
});

describe('formatEmailForClipboard', () => {
  it('writes the headers it has above a blank line', () => {
    const text = formatEmailForClipboard(
      draft({ to: ['a@b.com'], cc: ['c@d.com'], subject: 'Hi', body: 'Body.' }),
    );

    expect(text).toBe('To: a@b.com\nCc: c@d.com\nSubject: Hi\n\nBody.');
  });

  it('omits headers the draft does not carry', () => {
    expect(formatEmailForClipboard(draft({ subject: 'Hi', body: 'Body.' }))).toBe(
      'Subject: Hi\n\nBody.',
    );
  });

  it('is just the body when there are no headers at all', () => {
    expect(formatEmailForClipboard(draft({ body: 'Body.' }))).toBe('Body.');
  });
});

describe('toMailSafeText', () => {
  it('folds the curly quotes a model writes by default', () => {
    expect(toMailSafeText('I hope you’re well, “if that helps”')).toBe(
      `I hope you're well, "if that helps"`,
    );
  });

  it('spells every dash as a single hyphen, em and en alike', () => {
    expect(toMailSafeText('soon—let me know')).toBe('soon-let me know');
    expect(toMailSafeText('9–11am')).toBe('9-11am');
    expect(toMailSafeText('a−b')).toBe('a-b');
  });

  it('expands an ellipsis and flattens a non-breaking space', () => {
    expect(toMailSafeText('one moment…')).toBe('one moment...');
    expect(toMailSafeText('10 am')).toBe('10 am');
  });

  it('strips zero-width characters, which arrive as stray glyphs', () => {
    expect(toMailSafeText('a​b﻿c')).toBe('abc');
  });

  it('leaves accented letters alone: folding them would change the words', () => {
    expect(toMailSafeText('café naïve Über')).toBe('café naïve Über');
  });

  it('leaves text that is already ASCII untouched', () => {
    expect(toMailSafeText("Hi Alice, here's the plan -- see below.")).toBe(
      "Hi Alice, here's the plan -- see below.",
    );
  });
});

describe('the Outlook mojibake this folding exists for', () => {
  /** What a client that reads UTF-8 percent-escapes as cp1252 would display. */
  function asCp1252(url: string): string {
    const octets = [...(url.match(/%[0-9A-F]{2}/g) ?? [])].map((hex) => parseInt(hex.slice(1), 16));
    return new TextDecoder('windows-1252').decode(Uint8Array.from(octets));
  }

  it('no longer emits the multi-byte sequences Outlook mis-decodes', () => {
    const body = 'I hope you’re doing well—let me know when you’re free.';
    const url = buildMailtoUrl(draft({ body }));

    expect(url).not.toMatch(/%E2%80/);
    expect(asCp1252(url)).not.toContain('â€');
  });

  it('keeps the model typography on the Outlook web link, which decodes UTF-8 correctly', () => {
    const url = buildOutlookWebComposeUrl(draft({ subject: 'Q3 — review' }));

    expect(url).toContain('%E2%80%94');
  });

  it('keeps the model typography on the clipboard copy', () => {
    expect(formatEmailForClipboard(draft({ body: 'you’re' }))).toBe('you’re');
  });
});
