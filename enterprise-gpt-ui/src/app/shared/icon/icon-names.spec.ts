import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { ICON_NAMES, ICON_NAME_SET } from './icon-names';

const UI_ROOT = resolve(import.meta.dirname, '../../../..');
const SPRITE = join(UI_ROOT, 'public/icons/sprite.svg');

describe('icon manifest', () => {
  it('holds well-formed, unique, sorted glyph names', () => {
    const names = [...ICON_NAMES];

    expect(names.every((name) => /^bi-[a-z0-9]+(-[a-z0-9]+)*$/.test(name))).toBe(true);
    expect(new Set(names).size).toBe(names.length);
    expect(names).toEqual([...names].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)));
  });

  it('exposes the same names as a lookup set for the dev-mode guard', () => {
    expect(ICON_NAME_SET.size).toBe(ICON_NAMES.length);
    expect([...ICON_NAMES].every((name) => ICON_NAME_SET.has(name))).toBe(true);
  });

  it('matches the generated sprite exactly', () => {
    // Guards the failure the type system cannot see: a name added to the manifest
    // without re-running `npm run assets:icons` is typed but renders blank.
    expect(existsSync(SPRITE)).toBe(true);

    const sprite = readFileSync(SPRITE, 'utf8');
    const symbols = [...sprite.matchAll(/<symbol id="([^"]+)"/g)].map((match) => match[1]);

    expect(new Set(symbols)).toEqual(new Set(ICON_NAMES));
  });
});
