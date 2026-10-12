import { describe, expect, it } from 'vitest';
import { projectDetailFixture } from '@testing/projects';
import { toProjectBody } from './project-update';

describe('toProjectBody', () => {
  const project = projectDetailFixture({
    name: 'Helios',
    description: 'Cutover work',
    instructions: 'Cite Jira keys.',
  });

  it('echoes every field when only the name changes', () => {
    expect(toProjectBody(project, { name: 'Helios 2.4' })).toEqual({
      name: 'Helios 2.4',
      description: 'Cutover work',
      instructions: 'Cite Jira keys.',
    });
  });

  it('echoes everything when nothing changes', () => {
    expect(toProjectBody(project)).toEqual({
      name: 'Helios',
      description: 'Cutover work',
      instructions: 'Cite Jira keys.',
    });
  });

  it('distinguishes "leave it" from "clear it"', () => {
    expect(toProjectBody(project, { description: null }).description).toBeNull();
    expect(toProjectBody(project, { description: undefined }).description).toBe('Cutover work');
  });

  it('saves instructions without touching the name or the description', () => {
    expect(toProjectBody(project, { instructions: 'New rules.' })).toEqual({
      name: 'Helios',
      description: 'Cutover work',
      instructions: 'New rules.',
    });
  });
});
