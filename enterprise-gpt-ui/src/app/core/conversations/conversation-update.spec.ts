import { conversationFixture } from '@testing/conversations';
import { describe, expect, it } from 'vitest';
import { toUpdateBody } from './conversation-update';

describe('toUpdateBody', () => {
  const inProject = conversationFixture({
    name: 'Helios 2.4',
    projectId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
  });

  it('echoes the current projectId when only the name changes', () => {
    // The US-304 criterion this builder exists for: a rename that forgets the
    // project id would silently orphan the conversation.
    expect(toUpdateBody(inProject, { name: 'Renamed' })).toEqual({
      id: inProject.id,
      name: 'Renamed',
      projectId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    });
  });

  it('moves to the given project when one is named', () => {
    expect(toUpdateBody(inProject, { projectId: '11111111-2222-4333-8444-555555555555' })).toEqual({
      id: inProject.id,
      name: inProject.name,
      projectId: '11111111-2222-4333-8444-555555555555',
    });
  });

  it('unlinks only on an explicit null', () => {
    expect(toUpdateBody(inProject, { projectId: null }).projectId).toBeNull();
  });

  it('treats an explicitly-undefined projectId as omitted, not as an unlink', () => {
    // The natural US-307 call shape `{ projectId: selectedProject()?.id }` produces
    // exactly this value; it must echo, never remove.
    expect(toUpdateBody(inProject, { name: 'Renamed', projectId: undefined }).projectId).toBe(
      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    );
  });

  it('echoes everything for an empty change set', () => {
    expect(toUpdateBody(inProject)).toEqual({
      id: inProject.id,
      name: inProject.name,
      projectId: inProject.projectId,
    });
  });
});
