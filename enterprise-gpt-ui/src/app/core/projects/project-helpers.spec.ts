import { describe, expect, it } from 'vitest';
import { ValidationAppError } from '@core/errors/app-error';
import { projectDetailFixture } from '@testing/projects';
import { DUPLICATE_NAME_MESSAGE, projectNameMessages } from './duplicate-name';
import { toProjectBody } from './project-update';

function validationError(errors: Record<string, readonly string[]>): ValidationAppError {
  return {
    kind: 'validation-error',
    status: 400,
    title: 'One or more validation errors occurred.',
    message: 'One or more validation errors occurred.',
    detail: null,
    traceId: 'trace-1',
    instance: '/api/projects',
    url: '/api/projects',
    problem: null,
    cause: null,
    errors,
  };
}

describe('toProjectBody (US-903)', () => {
  const project = projectDetailFixture({
    name: 'Helios',
    description: 'Cutover work',
    instructions: 'Cite Jira keys.',
  });

  it('echoes every field when only the name changes', () => {
    // The trap the helper exists for: `PUT api/projects/{id}` assigns unconditionally,
    // so a body carrying `name` alone clears the description *and* the instructions.
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
    // `undefined` and `null` cannot be collapsed: a `??` here would make clearing a
    // description impossible.
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

describe('projectNameMessages (US-903)', () => {
  it('swaps the server duplicate message for the copy frame 4h requires', () => {
    const messages = projectNameMessages(
      validationError({ Name: ["A project named 'RFP Responses' already exists."] }),
    );

    expect(messages).toEqual([DUPLICATE_NAME_MESSAGE]);
  });

  it('renders every other name message verbatim', () => {
    const messages = projectNameMessages(
      validationError({ Name: ['Names are limited to 256 characters.'] }),
    );

    expect(messages).toEqual(['Names are limited to 256 characters.']);
  });

  it('reads the PascalCase key the server actually sends', () => {
    // `DictionaryKeyPolicy` is not set server-side, so `errors` keys stay verbatim
    // PascalCase while every DTO property is camelCase.
    expect(projectNameMessages(validationError({ Name: ['Required.'] }))).toEqual(['Required.']);
  });

  it('answers nothing when there is no problem', () => {
    expect(projectNameMessages(null)).toEqual([]);
  });
});
