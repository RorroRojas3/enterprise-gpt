import { describe, expect, it } from 'vitest';
import { conversationDetailFixture, conversationFixture } from '@testing/conversations';
import { conversationOf, exportTargetOf } from './chat-composer-host';

const ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';

describe('conversationOf', () => {
  it('is null before a conversation is bound', () => {
    expect(conversationOf(null, {}, conversationDetailFixture({ id: ID }))).toBeNull();
  });

  it('prefers the list copy, which a move patches optimistically', () => {
    const listed = conversationFixture({ id: ID, projectId: 'moved' });
    const detail = conversationDetailFixture({ id: ID, projectId: null });

    expect(conversationOf(ID, { [ID]: listed }, detail)).toBe(listed);
  });

  it('falls back to the detail for a conversation the sidebar never held', () => {
    const detail = conversationDetailFixture({ id: ID });

    expect(conversationOf(ID, {}, detail)).toBe(detail);
  });

  it('never borrows the detail of another conversation', () => {
    expect(conversationOf(ID, {}, conversationDetailFixture())).toBeNull();
  });

  /** A conversation the 50-row sidebar never held still has to name itself. */
  it('is null while neither the list nor the detail copy has landed', () => {
    expect(conversationOf(ID, {}, null)).toBeNull();
  });
});

describe('exportTargetOf', () => {
  it('is null for a conversation whose transcript is empty', () => {
    expect(exportTargetOf(conversationFixture({ id: ID }), false)).toBeNull();
  });

  it('is null with nothing to name the download after', () => {
    expect(exportTargetOf(null, true)).toBeNull();
  });

  it('names the conversation once its transcript has entries', () => {
    const conversation = conversationFixture({ id: ID, name: 'Helios release' });

    expect(exportTargetOf(conversation, true)).toEqual({ id: ID, name: 'Helios release' });
  });
});
