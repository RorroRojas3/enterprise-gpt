import { describe, expect, it } from 'vitest';
import { splitStreamingMarkdown, stripFenceInfo } from './streaming-split';

describe('splitStreamingMarkdown (US-602)', () => {
  it('puts everything in the tail when no boundary has arrived', () => {
    expect(splitStreamingMarkdown('Still the first paragraph')).toEqual({
      head: '',
      tail: 'Still the first paragraph',
    });
  });

  it('returns the empty split for empty text', () => {
    expect(splitStreamingMarkdown('')).toEqual({ head: '', tail: '' });
  });

  it('splits at the last blank line and leaves the delimiter on the head', () => {
    expect(splitStreamingMarkdown('P1\n\nP2')).toEqual({ head: 'P1\n\n', tail: 'P2' });
  });

  it('splits at the *last* boundary, not the first', () => {
    expect(splitStreamingMarkdown('P1\n\nP2\n\nP3')).toEqual({ head: 'P1\n\nP2\n\n', tail: 'P3' });
  });

  it('waits for the line after a trailing blank line before committing to it', () => {
    // The next line could turn out to be indented, which would make this a bad
    // place to split — so the boundary is not taken until it is known. Deferring
    // is what keeps the head from ever having to give ground.
    expect(splitStreamingMarkdown('P1\n\n')).toEqual({ head: '', tail: 'P1\n\n' });
    expect(splitStreamingMarkdown('P1\n\nP')).toEqual({ head: 'P1\n\n', tail: 'P' });
  });

  it('returns instead of hanging when the text opens on a blank line', () => {
    // `lastIndexOf` clamps a negative start to 0, so a rejected boundary at 0
    // used to re-select itself forever.
    expect(splitStreamingMarkdown('\n\n    code')).toEqual({ head: '', tail: '\n\n    code' });
    expect(splitStreamingMarkdown('\n\ntext')).toEqual({ head: '\n\n', tail: 'text' });
  });

  describe('an unclosed fence overrides the blank-line rule', () => {
    it('splits at the fence opener rather than the boundary before it', () => {
      expect(splitStreamingMarkdown('intro\n\n```ts\nconst x')).toEqual({
        head: 'intro\n\n',
        tail: '```ts\nconst x',
      });
    });

    it('ignores a blank line *inside* the unclosed fence', () => {
      expect(splitStreamingMarkdown('intro\n\n```ts\nconst x = 1;\n\nconst y')).toEqual({
        head: 'intro\n\n',
        tail: '```ts\nconst x = 1;\n\nconst y',
      });
    });

    it('treats a fence at the very start as all tail', () => {
      expect(splitStreamingMarkdown('```ts\nconst x')).toEqual({
        head: '',
        tail: '```ts\nconst x',
      });
    });

    it('counts an indented fence, up to three spaces', () => {
      expect(splitStreamingMarkdown('intro\n\n   ```\ncode')).toEqual({
        head: 'intro\n\n',
        tail: '   ```\ncode',
      });
    });
  });

  describe('a closed fence is content, not a boundary', () => {
    it('prefers a boundary that follows the closed fence', () => {
      const text = 'intro\n\n```ts\nconst x = 1;\n```\n\nAfter';

      expect(splitStreamingMarkdown(text)).toEqual({
        head: 'intro\n\n```ts\nconst x = 1;\n```\n\n',
        tail: 'After',
      });
    });

    it('never treats a blank line inside a closed fence as the boundary', () => {
      const text = 'intro\n\n```ts\nconst x = 1;\n\nconst y = 2;\n```\n\nAfter';

      expect(splitStreamingMarkdown(text)).toEqual({
        head: 'intro\n\n```ts\nconst x = 1;\n\nconst y = 2;\n```\n\n',
        tail: 'After',
      });
    });

    it('starts a block again after the fence closes, even with no blank line', () => {
      // Without this the boundary would fall back to the last blank line — of
      // which there is none here — and the head would collapse to nothing.
      const text = 'Here is an example:\n```js\nconst a = 1;\n```\nDone.';

      expect(splitStreamingMarkdown(text)).toEqual({
        head: 'Here is an example:\n```js\nconst a = 1;\n```\n',
        tail: 'Done.',
      });
    });
  });

  describe('tilde fences', () => {
    it('treats an unclosed tilde fence like a backtick one', () => {
      expect(splitStreamingMarkdown('intro\n\n~~~ts\nconst x = 1;\n\nconst y')).toEqual({
        head: 'intro\n\n',
        tail: '~~~ts\nconst x = 1;\n\nconst y',
      });
    });

    it('does not let a backtick line close a tilde block', () => {
      const text = 'intro\n\n~~~\n```\nstill inside\n\nnot a boundary';

      expect(splitStreamingMarkdown(text)).toEqual({
        head: 'intro\n\n',
        tail: '~~~\n```\nstill inside\n\nnot a boundary',
      });
    });

    it('does not let a shorter fence close a longer one', () => {
      const text = 'intro\n\n````md\n```\nnested\n\nstill inside';

      expect(splitStreamingMarkdown(text)).toEqual({
        head: 'intro\n\n',
        tail: '````md\n```\nnested\n\nstill inside',
      });
    });
  });

  describe('indented continuations', () => {
    it('refuses a boundary whose next line is indented', () => {
      // The two sides are parsed separately and marked re-bases indentation, so
      // an indented tail would render as prose instead of as an indented block.
      const text = 'Example:\n\n    code line one\n    code line two';

      expect(splitStreamingMarkdown(text)).toEqual({ head: '', tail: text });
    });

    it('refuses it for a nested list item too', () => {
      const text = 'Steps:\n\n  - Nested';

      expect(splitStreamingMarkdown(text)).toEqual({ head: '', tail: text });
    });
  });

  describe('the head only ever grows', () => {
    const sources = [
      'One\n\nTwo\n\n```ts\nconst a = 1;\n```\n\nThree',
      // No blank line before the fence, and none anywhere: the shape that used
      // to collapse the head to nothing the moment the fence closed.
      'Here is an example:\n```js\nconst a = 1;\n```\nDone.',
      'Prose first.\n\nThen:\n```sh\nnpm run build\n```\nAnd after.',
      '~~~python\nprint(1)\n~~~\ntail text',
      // Indented continuations: the shapes where a boundary is accepted while
      // the next line is still empty and would be withdrawn once it arrives.
      'Steps:\n\n  - Nested\n  - Also nested\n\nAfter the list.',
      'Example:\n\n    code line one\n    code line two\n\nProse again.',
      '\n\n    opens on a blank line',
    ];

    for (const source of sources) {
      it(`holds across every prefix of ${JSON.stringify(source.slice(0, 24))}…`, () => {
        let previous = '';

        for (let length = 1; length <= source.length; length += 1) {
          const prefix = source.slice(0, length);
          const { head, tail } = splitStreamingMarkdown(prefix);

          expect(head + tail).toBe(prefix);
          expect(prefix.startsWith(head)).toBe(true);
          expect(head.startsWith(previous)).toBe(true);
          previous = head;
        }
      });
    }
  });
});

describe('stripFenceInfo (US-602)', () => {
  it('removes the language from a fence', () => {
    expect(stripFenceInfo('```ts\nconst a = 1;')).toBe('```\nconst a = 1;');
  });

  it('removes it from every fence in the source', () => {
    expect(stripFenceInfo('```ts\na\n```\n\n```python\nb')).toBe('```\na\n```\n\n```\nb');
  });

  it('keeps a longer fence at its original length', () => {
    expect(stripFenceInfo('````md\n```\n')).toBe('````\n```\n');
  });

  it('leaves inline backticks alone', () => {
    expect(stripFenceInfo('Use `npm run build` to check.')).toBe('Use `npm run build` to check.');
  });

  it('is a no-op on empty text', () => {
    expect(stripFenceInfo('')).toBe('');
  });
});
