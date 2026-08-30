import { describe, expect, it } from 'vitest';
import { scanFences } from './fences';

describe('scanFences', () => {
  it('finds nothing in text with no fence', () => {
    expect(scanFences('Just prose.')).toEqual({ closed: [], open: null });
  });

  it('reports an unclosed fence as open, with its info string', () => {
    const { closed, open } = scanFences('```ts\nconst a = 1;');

    expect(closed).toEqual([]);
    expect(open).toMatchObject({ start: 0, marker: '```', info: 'ts' });
  });

  it('trims the info string and leaves a bare fence empty', () => {
    expect(scanFences('```  email  \nx\n```').closed[0]?.info).toBe('email');
    expect(scanFences('```\nx\n```').closed[0]?.info).toBe('');
  });

  it('spans the content between the two fence lines', () => {
    const text = '```email\nHi.\n```';
    const fence = scanFences(text).closed[0];

    expect(text.slice(fence?.contentStart, fence?.contentEnd)).toBe('Hi.\n');
  });

  it('closes only on the same marker character', () => {
    // A `~~~` cannot close a backtick fence, so this is one open block.
    expect(scanFences('```ts\nx\n~~~').open).not.toBeNull();
  });

  it('requires the closer to be at least as long as the opener', () => {
    const { closed, open } = scanFences('````ts\n```\nstill inside\n````');

    expect(open).toBeNull();
    expect(closed).toHaveLength(1);
  });

  it('lets a longer fence quote a shorter one without being closed by it', () => {
    const text = '````\n```ts\nx\n```\n````';
    const fence = scanFences(text).closed[0];

    expect(text.slice(fence?.contentStart, fence?.contentEnd)).toBe('```ts\nx\n```\n');
  });

  it('allows up to three spaces of indent, and no more', () => {
    expect(scanFences('   ```ts\nx\n   ```').closed).toHaveLength(1);
    expect(scanFences('    ```ts\nx\n    ```').closed).toEqual([]);
  });

  it('records the info string of the line that closed the block', () => {
    // CommonMark forbids an info string on a closer, so a non-empty one means the
    // block was closed by what was meant to be a nested opener.
    expect(scanFences('```email\nx\n```ts\ny\n```').closed[0]?.closerInfo).toBe('ts');
    expect(scanFences('```email\nx\n```').closed[0]?.closerInfo).toBe('');
  });

  it('reports several blocks in source order', () => {
    expect(scanFences('```a\n1\n```\n\n```b\n2\n```').closed.map((f) => f.info)).toEqual([
      'a',
      'b',
    ]);
  });
});
