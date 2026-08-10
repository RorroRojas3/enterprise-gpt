// Shared helpers for the static source checks. Kept out of the individual scripts
// so `check-forbidden-apis` and `check-icon-names` cannot drift on what counts as a
// comment — a name that appears only in prose must not fail either of them.
import { readdirSync } from 'node:fs';
import { join } from 'node:path';

/** Every file under `dir`, recursively. */
export function walk(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? walk(path) : [path];
  });
}

/**
 * Blanks out comments while preserving line and column positions, so a match's line
 * number still refers to the original file.
 *
 * String-literal awareness is the point: without it the `//` in a URL swallows the
 * rest of the line, and a check that silently stops scanning is worse than no check.
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
