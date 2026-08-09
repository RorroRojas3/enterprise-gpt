import { describe, expect, it } from 'vitest';
import { toCamelCase } from './camel-case';

describe('toCamelCase', () => {
  // Every expectation is what System.Text.Json's JsonNamingPolicy.CamelCase
  // produces, which is what the server used to name the JSON the form binds to.
  it.each([
    ['Name', 'name'],
    ['ContextWindowSize', 'contextWindowSize'],
    ['ID', 'id'],
    ['URLValue', 'urlValue'],
    ['IOStream', 'ioStream'],
    ['IPhone', 'iPhone'],
    ['I', 'i'],
    ['XMLHttpRequest', 'xmlHttpRequest'],
    ['alreadyCamel', 'alreadyCamel'],
    ['lowercase', 'lowercase'],
    ['', ''],
    ['_Private', '_Private'],
    // Unreachable through FluentValidation property paths, but .NET's space
    // branch is mirrored so the two implementations cannot diverge silently.
    ['AB C', 'ab C'],
  ])('converts %s to %s', (input, expected) => {
    expect(toCamelCase(input)).toBe(expected);
  });
});
