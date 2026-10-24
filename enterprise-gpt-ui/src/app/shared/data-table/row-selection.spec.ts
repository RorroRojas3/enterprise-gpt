import { describe, expect, it } from 'vitest';
import { selectRows, toggleRow } from './row-selection';

describe('row-selection', () => {
  it('always returns a new Set, so the signal write is actually seen', () => {
    const first = new Set(['a']);

    expect(toggleRow(first, 'b')).not.toBe(first);
    expect(selectRows(first, ['c'])).not.toBe(first);
  });

  it('toggles membership both ways', () => {
    expect([...toggleRow(new Set(), 'a')]).toEqual(['a']);
    expect([...toggleRow(new Set(['a']), 'a')]).toEqual([]);
  });
});
