import { describe, expect, it } from 'vitest';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import {
  ProblemDetails,
  errorsExtension,
  numberExtension,
  parseProblemDetails,
  stringArrayExtension,
  stringExtension,
} from './problem-details';

function problem(extra: Record<string, unknown> = {}): ProblemDetails {
  return { type: '/problems/forbidden', title: 'Access denied', status: 403, ...extra };
}

describe('parseProblemDetails', () => {
  it('accepts every fixture', () => {
    for (const fixture of Object.values(PROBLEM_FIXTURES)) {
      expect(parseProblemDetails(fixture)).not.toBeNull();
    }
  });

  it.each([
    ['null', null],
    ['undefined', undefined],
    ['an array', []],
    ['a string', '{"type":"x"}'],
    ['a number', 400],
    ['an empty object', {}],
    ['a body with no type', { status: 400 }],
    ['a body with no status', { type: '/problems/forbidden' }],
    ['a body whose status is a string', { type: '/problems/forbidden', status: '403' }],
  ])('rejects %s', (_label, value) => {
    expect(parseProblemDetails(value)).toBeNull();
  });

  it('rejects the parse-failure wrapper HttpClient supplies for a non-JSON body', () => {
    expect(
      parseProblemDetails({ error: new SyntaxError('bad'), text: '<html></html>' }),
    ).toBeNull();
  });
});

describe('extension readers', () => {
  it('reads a string extension and rejects a wrong-typed one', () => {
    expect(stringExtension(problem({ serverName: 'Weather' }), 'serverName')).toBe('Weather');
    expect(stringExtension(problem({ serverName: 7 }), 'serverName')).toBeNull();
    expect(stringExtension(problem(), 'serverName')).toBeNull();
  });

  it('reads a number extension and rejects a non-finite one', () => {
    expect(numberExtension(problem({ maxBytes: 1024 }), 'maxBytes')).toBe(1024);
    expect(numberExtension(problem({ maxBytes: '1024' }), 'maxBytes')).toBeNull();
    expect(numberExtension(problem({ maxBytes: Number.NaN }), 'maxBytes')).toBeNull();
    expect(numberExtension(problem({ maxBytes: Number.POSITIVE_INFINITY }), 'maxBytes')).toBeNull();
  });

  it('reads a string-array extension, dropping non-string elements', () => {
    expect(
      stringArrayExtension(problem({ permissions: ['Administrator', 7] }), 'permissions'),
    ).toEqual(['Administrator']);
    expect(stringArrayExtension(problem({ permissions: 'Administrator' }), 'permissions')).toEqual(
      [],
    );
    expect(stringArrayExtension(problem(), 'permissions')).toEqual([]);
  });

  it('reads the errors dictionary, dropping malformed entries', () => {
    const errors = errorsExtension(
      problem({ errors: { Name: ['required', 7], Other: 'not an array', Third: [] } }),
    );

    expect(errors['Name']).toEqual(['required']);
    expect(errors['Third']).toEqual([]);
    expect(errors).not.toHaveProperty('Other');
  });

  it.each([
    ['a missing errors member', undefined],
    ['a null errors member', null],
    ['an array errors member', []],
  ])('returns an empty dictionary for %s', (_label, value) => {
    expect(errorsExtension(problem({ errors: value }))).toEqual({});
  });
});
