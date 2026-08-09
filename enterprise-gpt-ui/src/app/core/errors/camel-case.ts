/**
 * Reimplements `System.Text.Json.JsonNamingPolicy.CamelCase`.
 *
 * The API camel-cases DTO property names on the wire but writes dictionary keys
 * verbatim, so a validation problem's `errors` keys arrive PascalCase while the
 * form controls bound to those same properties are named camelCase. Matching them
 * means applying the server's own algorithm rather than a naive `toLowerCase` of
 * the first character, which would turn `IOStream` into `iOStream` where .NET
 * produces `ioStream`.
 *
 * @param name A single property-name segment.
 */
export function toCamelCase(name: string): string {
  if (name.length === 0 || !isUpper(name[0] as string)) {
    return name;
  }

  const chars = [...name];
  for (let index = 0; index < chars.length; index++) {
    const current = chars[index] as string;

    if (index > 0 && !isUpper(current)) {
      break;
    }

    // The last character of a run of capitals stays capital when it begins the
    // next word: `URLValue` -> `urlValue`, but `URL` -> `url`.
    const next = chars[index + 1];
    if (index > 0 && next !== undefined && !isUpper(next)) {
      // .NET lowercases the current character first when the next one is a space.
      // Unreachable through property names, but kept so the two stay identical.
      if (next === ' ') {
        chars[index] = current.toLowerCase();
      }
      break;
    }

    chars[index] = current.toLowerCase();
  }

  return chars.join('');
}

function isUpper(char: string): boolean {
  return char !== char.toLowerCase() && char === char.toUpperCase();
}
