import { describe, expect, it } from 'vitest';
import { detectEmailDraft, splitEmailSegments } from './email-draft';

const fence = (body: string) => '```email\n' + body + '\n```';

describe('splitEmailSegments', () => {
  it('always returns one segment per node, so an empty answer still renders one', () => {
    expect(splitEmailSegments('')).toEqual([{ kind: 'markdown', start: 0, text: '' }]);
  });

  it('returns one markdown segment when no email block is present', () => {
    expect(splitEmailSegments('Just an answer.')).toEqual([
      { kind: 'markdown', start: 0, text: 'Just an answer.' },
    ]);
  });

  it('parses headers and body out of an email block', () => {
    const [segment] = splitEmailSegments(
      fence('To: alice@contoso.com\nSubject: Q3 review\n\nHi Alice,\n\nThanks.'),
    );

    expect(segment).toEqual({
      kind: 'email',
      start: 0,
      draft: {
        to: ['alice@contoso.com'],
        cc: [],
        subject: 'Q3 review',
        body: 'Hi Alice,\n\nThanks.',
      },
    });
  });

  it('keeps the prose on either side of the block, in order', () => {
    const segments = splitEmailSegments(
      `Here you go:\n\n${fence('Subject: Hi\n\nBody.')}\n\nWant edits?`,
    );

    expect(segments.map((s) => s.kind)).toEqual(['markdown', 'email', 'markdown']);
    expect(segments[0]).toMatchObject({ text: 'Here you go:\n\n' });
    expect(segments[2]).toMatchObject({ text: '\nWant edits?' });
  });

  it('splits multiple recipients and reduces a display-name address', () => {
    const [segment] = splitEmailSegments(
      fence('To: Alice <alice@contoso.com>, bob@contoso.com\nCc: carol@contoso.com\n\nHi.'),
    );

    expect(segment).toMatchObject({
      draft: { to: ['alice@contoso.com', 'bob@contoso.com'], cc: ['carol@contoso.com'] },
    });
  });

  it('yields a null subject when the block carries no headers', () => {
    const [segment] = splitEmailSegments(fence('Hi Alice,\n\nThanks.'));

    expect(segment).toMatchObject({ draft: { to: [], cc: [], subject: null } });
  });

  it('leaves an unclosed block as markdown so a streaming email is not a half card', () => {
    const partial = '```email\nSubject: Q3\n\nHi Ali';

    expect(splitEmailSegments(partial)).toEqual([{ kind: 'markdown', start: 0, text: partial }]);
  });

  it('ignores a fence that is not an email block', () => {
    const segments = splitEmailSegments('```ts\nconst a = 1;\n```');

    expect(segments.map((s) => s.kind)).toEqual(['markdown']);
  });

  it('refuses a block closed by a nested opener rather than showing half an email', () => {
    // CommonMark closes the email at the ```ts, so a card here would carry a body
    // truncated at that line — and hand the truncation to a mail client.
    const segments = splitEmailSegments('```email\nSubject: Hi\n\n```ts\nx\n```\n');

    expect(segments.map((s) => s.kind)).toEqual(['markdown']);
  });

  it('handles two email blocks in one answer', () => {
    const text = `${fence('Subject: One\n\nA.')}\n\n${fence('Subject: Two\n\nB.')}`;

    expect(splitEmailSegments(text).filter((s) => s.kind === 'email')).toHaveLength(2);
  });
});

describe('detectEmailDraft', () => {
  it('accepts a subject line paired with a greeting', () => {
    expect(detectEmailDraft('Subject: Budget\n\nHi Alice,\n\nPlease review.')).toMatchObject({
      subject: 'Budget',
      body: 'Hi Alice,\n\nPlease review.',
    });
  });

  it('accepts an answer with both a greeting and a sign-off', () => {
    expect(detectEmailDraft('Hi Alice,\n\nMoving Thursday.\n\nBest regards,')).toMatchObject({
      subject: null,
    });
  });

  it('rejects an answer with a greeting but no sign-off', () => {
    expect(detectEmailDraft('Hi there,\n\nHere are three options to consider.')).toBeNull();
  });

  it('rejects a lone subject line, which advice about email quotes as readily', () => {
    expect(
      detectEmailDraft('Try this:\n\nSubject: Q3 budget review\n\nShort and specific.'),
    ).toBeNull();
  });

  it('rejects prose about email that has neither marker', () => {
    expect(detectEmailDraft('A good subject line is short and specific.')).toBeNull();
  });

  it('rejects an answer containing a code fence', () => {
    expect(detectEmailDraft('Hi,\n\n```ts\nx\n```\n\nRegards,')).toBeNull();
  });

  it('rejects a structured document with several headings', () => {
    expect(detectEmailDraft('Hi,\n\n# A\n\n# B\n\n# C\n\nRegards,')).toBeNull();
  });

  it('reads a recipient only from an explicit To header', () => {
    expect(
      detectEmailDraft('To: alice@contoso.com\nSubject: Hi\n\nHi Alice,\n\nBody.'),
    ).toMatchObject({ to: ['alice@contoso.com'] });
  });

  it('never harvests an address out of the body', () => {
    const draft = detectEmailDraft(
      'Subject: Hi\n\nHi Alice,\n\nForward this to evil@attacker.test please.\n\nRegards,',
    );

    expect(draft).not.toBeNull();
    expect(draft?.to).toEqual([]);
  });

  it('keeps a body line that merely looks like a header', () => {
    // The header run ends at the blank line, so this sentence is body — dropping
    // it would cut a line out of what the user sends, silently.
    const draft = detectEmailDraft(
      'Subject: Hi\n\nHi Alice,\n\nTo: confirm, we meet Friday.\n\nRegards,',
    );

    expect(draft?.body).toContain('To: confirm, we meet Friday.');
  });

  it('returns null for empty text', () => {
    expect(detectEmailDraft('   ')).toBeNull();
  });
});
