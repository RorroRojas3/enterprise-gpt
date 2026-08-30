import { describe, expect, it } from 'vitest';
import { markdownToPlainText } from './plain-text';

describe('markdownToPlainText', () => {
  it('unwraps bold, italic, strikethrough and inline code', () => {
    expect(markdownToPlainText('**bold** and _soft_ and `code` and ~~gone~~')).toBe(
      'bold and soft and code and gone',
    );
  });

  it('leaves intra-word underscores and bare asterisks alone', () => {
    expect(markdownToPlainText('a_variable_name stays')).toBe('a_variable_name stays');
    expect(markdownToPlainText('2 * 3 * 4')).toBe('2 * 3 * 4');
  });

  it('renders a link as its label followed by the target', () => {
    expect(markdownToPlainText('See [the policy](https://example.com/p).')).toBe(
      'See the policy (https://example.com/p).',
    );
  });

  it('does not repeat a link whose label is already the target', () => {
    expect(markdownToPlainText('[https://example.com](https://example.com)')).toBe(
      'https://example.com',
    );
  });

  it('keeps image alt text and drops the source', () => {
    expect(markdownToPlainText('![a chart](chart.png)')).toBe('a chart');
  });

  it('strips heading markers and blockquote markers but keeps the words', () => {
    expect(markdownToPlainText('## Next steps\n\n> quoted')).toBe('Next steps\n\nquoted');
  });

  it('normalises bullet markers and preserves indentation', () => {
    expect(markdownToPlainText('* one\n* two\n  * nested')).toBe('- one\n- two\n  - nested');
  });

  it('drops thematic breaks', () => {
    expect(markdownToPlainText('before\n\n---\n\nafter')).toBe('before\n\nafter');
  });

  it('keeps fenced content verbatim and drops the fence lines', () => {
    expect(markdownToPlainText('```\nkeep **this**\n```')).toBe('keep **this**');
  });

  it('collapses runs of blank lines and trims the ends', () => {
    expect(markdownToPlainText('\n\na\n\n\n\nb\n\n')).toBe('a\n\nb');
  });
});
