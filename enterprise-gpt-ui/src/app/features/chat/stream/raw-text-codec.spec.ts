import { describe, expect, it } from 'vitest';
import { encode, splitBytes } from '@testing/stream-frames';
import { createRawTextCodec } from './raw-text-codec';
import { DEFAULT_EVENT_TIMESTAMP } from './stream-codec';

describe('createRawTextCodec', () => {
  it('synthesizes one TextDelta per chunk so downstream folding is uniform', () => {
    const codec = createRawTextCodec();

    expect(codec.decode(encode('Hello, world.'))).toEqual([
      {
        kind: 'TextDelta',
        text: 'Hello, world.',
        depth: 0,
        toolKind: 'Unknown',
        timestamp: DEFAULT_EVENT_TIMESTAMP,
      },
    ]);
  });

  it('carries a multi-byte character split across chunks without corruption', () => {
    const codec = createRawTextCodec();
    const [first, second] = splitBytes(encode('a🌍b'), 3);

    const decoded = [...codec.decode(first), ...codec.decode(second)];

    expect(decoded.map((event) => event.text).join('')).toBe('a🌍b');
    expect(decoded.map((event) => event.text).join('')).not.toContain('�');
  });

  it('returns nothing for an empty chunk', () => {
    const codec = createRawTextCodec();

    expect(codec.decode(encode(''))).toEqual([]);
  });

  it('flushes a genuinely truncated trailing sequence as a replacement character', () => {
    const codec = createRawTextCodec();

    // Only the first two bytes of U+1F30D ever arrive: nothing to emit while
    // streaming, and end-of-stream resolves the incomplete sequence to U+FFFD.
    expect(codec.decode(encode('🌍').slice(0, 2))).toEqual([]);

    expect(
      codec
        .flush()
        .map((event) => event.text)
        .join(''),
    ).toBe('�');
  });
});
