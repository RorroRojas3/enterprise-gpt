import { describe, expect, it } from 'vitest';
import { formatBytes } from './bytes';

describe('formatBytes', () => {
  it('writes the upload cap exactly as the server does', () => {
    // MaxUploadSizeEndpointFilter.FormatSize renders the same limit as "50 MB", and
    // US-805's toast quotes both sides of the comparison in one sentence.
    expect(formatBytes(52_428_800)).toBe('50 MB');
  });

  it('keeps one decimal for a non-integer megabyte count', () => {
    expect(formatBytes(2_516_582)).toBe('2.4 MB');
  });

  it('falls back to kilobytes below one megabyte', () => {
    expect(formatBytes(831_488)).toBe('812 KB');
  });

  it('renders a tiny file as whole kilobytes, not as "0.0 KB"', () => {
    expect(formatBytes(0)).toBe('0 KB');
    expect(formatBytes(1)).toBe('0 KB');
    expect(formatBytes(1600)).toBe('2 KB');
  });
});
