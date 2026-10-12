import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { BRAND_ICON_NAMES, BRAND_ICON_NAME_SET } from './brand-icon-names';
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
});

describe('brand icon manifest', () => {
  it('holds well-formed, unique, sorted brand names', () => {
    const names = [...BRAND_ICON_NAMES];

    expect(names.every((name) => /^brand-[a-z0-9]+(-[a-z0-9]+)*$/.test(name))).toBe(true);
    expect(new Set(names).size).toBe(names.length);
    expect(names).toEqual([...names].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)));
  });

  it('exposes the same names as a lookup set for the dev-mode guard', () => {
    expect(BRAND_ICON_NAME_SET.size).toBe(BRAND_ICON_NAMES.length);
    expect([...BRAND_ICON_NAMES].every((name) => BRAND_ICON_NAME_SET.has(name))).toBe(true);
  });

  it('keeps the two lanes disjoint', () => {
    // One sprite, one id namespace: a `brand-` name colliding with a `bi-` one
    // would silently win or lose depending on emission order.
    const overlap = [...BRAND_ICON_NAMES].filter((name) => ICON_NAME_SET.has(name));

    expect(overlap).toEqual([]);
  });
});

describe('the generated sprite', () => {
  it('holds exactly the two manifests, and nothing else', () => {
    // Guards the failure the type system cannot see: a name added to either
    // manifest without re-running `npm run assets:icons` is typed but renders blank.
    expect(existsSync(SPRITE)).toBe(true);

    const sprite = readFileSync(SPRITE, 'utf8');
    const symbols = [...sprite.matchAll(/<symbol id="([^"]+)"/g)].map((match) => match[1]);

    expect(new Set(symbols)).toEqual(new Set<string>([...ICON_NAMES, ...BRAND_ICON_NAMES]));
  });

  it('leaves every brand mark its own fills, and forces currentColor on nothing else', () => {
    // The single fact separating the two lanes. A brand symbol that picked up
    // `fill="currentColor"` would render as a flat silhouette in the menu.
    const sprite = readFileSync(SPRITE, 'utf8');
    const symbols = [...sprite.matchAll(/<symbol id="([^"]+)"([^>]*)>/g)];

    expect(symbols.length).toBeGreaterThan(0);
    for (const match of symbols) {
      const id = match[1] ?? '';
      const attrs = match[2] ?? '';
      expect(attrs.includes('fill="currentColor"')).toBe(id.startsWith('bi-'));
    }
  });
});
