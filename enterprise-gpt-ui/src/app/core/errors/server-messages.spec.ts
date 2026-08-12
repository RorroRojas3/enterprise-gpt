import { describe, expect, it } from 'vitest';
import { ValidationAppError } from './app-error';
import { serverMessagesFor, unmatchedServerMessages } from './server-messages';

function validationError(errors: Record<string, readonly string[]>): ValidationAppError {
  return {
    kind: 'validation-error',
    status: 400,
    title: 'One or more validation errors occurred.',
    detail: null,
    traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
    instance: '/api/conversations',
    url: 'https://api.example.test/api/conversations',
    message: 'One or more validation errors occurred.',
    problem: null,
    cause: null,
    errors,
  };
}

describe('serverMessagesFor', () => {
  it('matches the server PascalCase key through the .NET camel-casing algorithm', () => {
    const error = validationError({
      Name: ["'Name' must not be empty."],
      ContextWindowSize: ['Too small.'],
    });

    expect(serverMessagesFor(error, 'name')).toEqual(["'Name' must not be empty."]);
  });

  it('falls back to a case-insensitive match for keys camel-casing cannot map', () => {
    expect(serverMessagesFor(validationError({ NAME: ['Shouted.'] }), 'name')).toEqual([
      'Shouted.',
    ]);
  });

  it('collects across keys that normalize to the same field', () => {
    const error = validationError({ Name: ['First.'], NAME: ['Second.'] });

    expect(serverMessagesFor(error, 'name')).toEqual(['First.', 'Second.']);
  });

  it('returns nothing for a null error or an unrelated field', () => {
    expect(serverMessagesFor(null, 'name')).toEqual([]);
    expect(serverMessagesFor(validationError({ Name: ['x'] }), 'projectId')).toEqual([]);
  });
});

describe('unmatchedServerMessages', () => {
  it('returns messages under keys no listed field claims, object-level ones included', () => {
    const error = validationError({
      Name: ['Matched.'],
      // FluentValidation keys object-level rules with the empty string.
      '': ['The update is not allowed right now.'],
      ProjectId: ['Unknown project.'],
    });

    expect(unmatchedServerMessages(error, ['name'])).toEqual([
      'The update is not allowed right now.',
      'Unknown project.',
    ]);
  });

  it('returns nothing when every key matched, or for a null error', () => {
    expect(unmatchedServerMessages(validationError({ Name: ['x'] }), ['name'])).toEqual([]);
    expect(unmatchedServerMessages(null, ['name'])).toEqual([]);
  });
});
