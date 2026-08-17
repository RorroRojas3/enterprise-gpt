import { describe, expect, it } from 'vitest';
import { formatContext, formatTokens } from './model-format';

describe('formatContext', () => {
  it('abbreviates as frame `5e` writes it', () => {
    expect(formatContext(400_000)).toBe('400k');
    expect(formatContext(200_000)).toBe('200k');
    expect(formatContext(128_000)).toBe('128k');
    expect(formatContext(512_000)).toBe('512k');
  });

  it('leaves a value below a thousand alone', () => {
    expect(formatContext(900)).toBe('900');
  });

  it('keeps one decimal for a fraction of a thousand', () => {
    // The column is decimal(18,2), so a model seeded outside this screen can hold one.
    expect(formatContext(128_500)).toBe('128.5k');
  });

  it('renders a dash rather than NaN', () => {
    expect(formatContext(Number.NaN)).toBe('—');
  });
});

describe('formatTokens', () => {
  it('groups thousands as frame `5e` writes them', () => {
    expect(formatTokens(32_768)).toBe('32,768');
    expect(formatTokens(64_000)).toBe('64,000');
    expect(formatTokens(8_192)).toBe('8,192');
  });

  it('uses one fixed grouping rather than the reader’s locale', () => {
    // The app ships no i18n, so a locale-dependent separator would put `32.768` beside a
    // deployment name and a context size that are not localised at all.
    expect(formatTokens(1_000_000)).toBe('1,000,000');
  });

  it('renders a dash rather than NaN', () => {
    expect(formatTokens(Number.NaN)).toBe('—');
  });
});
