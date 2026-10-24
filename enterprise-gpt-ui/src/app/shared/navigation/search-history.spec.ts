import { describe, expect, it } from 'vitest';
import { shouldReplaceHistory } from './search-history';

describe('shouldReplaceHistory', () => {
  it('pushes when a filter goes on, so back returns to the unfiltered list', () => {
    expect(shouldReplaceHistory('', 'quarterly')).toBe(false);
  });

  it('pushes when a filter comes off, so clearing is undoable too', () => {
    expect(shouldReplaceHistory('quarterly', '')).toBe(false);
  });

  it('replaces while a term is being refined', () => {
    expect(shouldReplaceHistory('quar', 'quarterly')).toBe(true);
  });
});
