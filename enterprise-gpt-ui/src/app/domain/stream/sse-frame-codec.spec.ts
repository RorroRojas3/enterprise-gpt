import { describe, expect, it } from 'vitest';
import { assistantEvent, encode, frame, fullTurnEvents, splitBytes } from '@testing/stream-frames';
import { createSseFrameCodec } from './sse-frame-codec';

describe('createSseFrameCodec', () => {
  it('decodes one frame in one chunk, stripping the data: prefix', () => {
    const codec = createSseFrameCodec();
    const event = assistantEvent('TextDelta');

    expect(codec.decode(encode(frame(event)))).toEqual([event]);
  });

  it('decodes many frames in one chunk, in arrival order', () => {
    const codec = createSseFrameCodec();
    const events = fullTurnEvents();

    const decoded = codec.decode(encode(events.map(frame).join('')));

    expect(decoded).toEqual(events);
  });

  it('carries a frame split mid-JSON across two reads and parses it once, intact', () => {
    const codec = createSseFrameCodec();
    const event = assistantEvent('ActivityStarted');
    const bytes = encode(frame(event));
    // Split inside the JSON payload, well past the `data: ` prefix.
    const [first, second] = splitBytes(bytes, Math.floor(bytes.length / 2));

    expect(codec.decode(first)).toEqual([]);
    expect(codec.decode(second)).toEqual([event]);
  });

  it('carries a frame split between the two newlines of the separator', () => {
    const codec = createSseFrameCodec();
    const event = assistantEvent('Status');
    const bytes = encode(frame(event));

    expect(codec.decode(bytes.slice(0, bytes.length - 1))).toEqual([]);
    expect(codec.decode(bytes.slice(bytes.length - 1))).toEqual([event]);
  });

  it('reassembles a multi-byte character split across a chunk boundary', () => {
    const codec = createSseFrameCodec();
    const event = assistantEvent('TextDelta', { text: 'Hi 🌍!' });
    const text = frame(event);
    const bytes = encode(text);
    // Everything before the emoji is ASCII, so its string index is its byte
    // index. U+1F30D is four UTF-8 bytes; splitting two bytes into it makes a
    // stateless decoder emit U+FFFD replacement characters.
    const [first, second] = splitBytes(bytes, text.indexOf('🌍') + 2);

    const decoded = [...codec.decode(first), ...codec.decode(second)];

    expect(decoded).toEqual([event]);
  });

  it('ignores a frame whose kind is unknown and keeps decoding', () => {
    const codec = createSseFrameCodec();
    const known = assistantEvent('TextDelta');
    const unknown = `data: ${JSON.stringify({ ...assistantEvent('Status'), kind: 'ToolPreamble' })}\n\n`;

    expect(codec.decode(encode(unknown + frame(known)))).toEqual([known]);
  });

  it('skips a malformed JSON frame and keeps decoding', () => {
    const codec = createSseFrameCodec();
    const known = assistantEvent('Finished');

    expect(codec.decode(encode('data: {"kind":"TextDelta",\n\n' + frame(known)))).toEqual([known]);
  });

  it('skips a frame without the data: prefix', () => {
    const codec = createSseFrameCodec();
    const known = assistantEvent('TextDelta');

    expect(codec.decode(encode(': keep-alive\n\n' + frame(known)))).toEqual([known]);
  });

  it('returns nothing for an empty chunk and skips an empty payload', () => {
    const codec = createSseFrameCodec();

    expect(codec.decode(encode(''))).toEqual([]);
    expect(codec.decode(encode('data: \n\n'))).toEqual([]);
  });

  it('skips a payload that is valid JSON but not an object', () => {
    const codec = createSseFrameCodec();

    expect(codec.decode(encode('data: 42\n\ndata: null\n\ndata: "TextDelta"\n\n'))).toEqual([]);
  });

  it('salvages a complete trailing frame that lost its terminator, on flush', () => {
    const codec = createSseFrameCodec();
    const event = assistantEvent('Finished');
    const truncated = frame(event).slice(0, -2);

    expect(codec.decode(encode(truncated))).toEqual([]);
    expect(codec.flush()).toEqual([event]);
  });

  it('drops a tail truncated mid-JSON, on flush', () => {
    const codec = createSseFrameCodec();
    const partial = frame(assistantEvent('TextDelta')).slice(0, 20);

    expect(codec.decode(encode(partial))).toEqual([]);
    expect(codec.flush()).toEqual([]);
  });
});
