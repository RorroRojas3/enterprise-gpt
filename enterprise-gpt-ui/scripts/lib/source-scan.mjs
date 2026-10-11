// Shared so `check-forbidden-apis` and `check-icon-names` agree on what counts as a comment.
import { readdirSync } from 'node:fs';
import { join } from 'node:path';

export function walk(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? walk(path) : [path];
  });
}

/**
 * Blanks comments in place so match positions still refer to the original file. String-aware,
 * so the `//` in a URL cannot silently swallow the rest of the line.
 */
export function stripComments(source, { html = false } = {}) {
  const blank = (match) => match.replace(/[^\n]/g, ' ');

  if (html) {
    return source.replace(/<!--[\s\S]*?-->/g, blank);
  }

  let out = '';
  let state = 'code';

  for (let index = 0; index < source.length; index++) {
    const char = source[index];
    const next = source[index + 1];

    if (state === 'code') {
      if (char === '/' && next === '/') {
        state = 'line';
        out += '  ';
        index++;
      } else if (char === '/' && next === '*') {
        state = 'block';
        out += '  ';
        index++;
      } else if (char === "'" || char === '"' || char === '`') {
        state = char;
        out += char;
      } else {
        out += char;
      }
    } else if (state === 'line') {
      state = char === '\n' ? 'code' : 'line';
      out += char === '\n' ? char : ' ';
    } else if (state === 'block') {
      if (char === '*' && next === '/') {
        state = 'code';
        out += '  ';
        index++;
      } else {
        out += char === '\n' ? char : ' ';
      }
    } else {
      out += char;
      if (char === '\\') {
        out += source[index + 1] ?? '';
        index++;
      } else if (char === state) {
        state = 'code';
      }
    }
  }

  return out;
}
