import { describe, expect, it } from 'vitest';
import { toCamelCase } from './camel-case';

describe('toCamelCase', () => {
  // Every expectation is what System.Text.Json's JsonNamingPolicy.CamelCase produces.
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
    // Unreachable through property paths; pins the mirrored .NET space branch.
    ['AB C', 'ab C'],
  ])('converts %s to %s', (input, expected) => {
    expect(toCamelCase(input)).toBe(expected);
  });
});
