import { describe, expect, it } from 'vitest';
import { PAGE_GAP, pageWindow } from './page-window';

describe('pageWindow', () => {
  it('shows every page when they all fit', () => {
    expect(pageWindow(1, 4)).toEqual([1, 2, 3, 4]);
  });

  it('gaps the far end when the current page is at the start', () => {
    expect(pageWindow(1, 13)).toEqual([1, 2, PAGE_GAP, 13]);
  });

  it('gaps both ends when the current page is in the middle', () => {
    expect(pageWindow(7, 13)).toEqual([1, PAGE_GAP, 6, 7, 8, PAGE_GAP, 13]);
  });

  it('renders a hole of exactly one page as that page, not an ellipsis', () => {
    expect(pageWindow(4, 6)).toEqual([1, 2, 3, 4, 5, 6]);
  });

  it('handles the degenerate counts', () => {
    expect(pageWindow(1, 0)).toEqual([]);
    expect(pageWindow(1, 1)).toEqual([1]);
  });
});
