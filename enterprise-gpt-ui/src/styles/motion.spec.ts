import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * jsdom applies no stylesheet, so suppression cannot be observed at the DOM. These pin the
 * source instead: the block, its `!important` and its position, which no lint gate checks.
 */

const UI_ROOT = resolve(import.meta.dirname, '../..');
const STYLES = join(UI_ROOT, 'src/styles');

/** The app's own keyframes; Bootstrap's run only through its classes, never an `animation` here. */
const KEYFRAMES = ['blink', 'ringpulse', 'ridgedash'];

function scssFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) {
      return scssFiles(path);
    }
    return path.endsWith('.scss') ? [path] : [];
  });
}

/** Blanks line and block comments, so prose about motion is never read as CSS. */
function withoutComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/.*$/gm, '$1');
}

describe('the reduced-motion contract', () => {
  it('suppresses every animation and transition under the preference', () => {
    const motion = withoutComments(readFileSync(join(STYLES, '_motion.scss'), 'utf8'));
    const at = motion.indexOf('@media (prefers-reduced-motion: reduce)');

    // Guarded: `slice(-1)` on a missing block yields one character, and the absence would
    // surface as a puzzling `toContain` failure.
    expect(at, 'the reduced-motion block is missing entirely').toBeGreaterThanOrEqual(0);
    const block = motion.slice(at);
    // Universal, including generated content: a caret and the code-block chrome are
    // both `::after`.
    expect(block).toContain('*::before');
    expect(block).toContain('*::after');

    // `!important` on each: this block exists to defeat authored declarations, every
    // one of which is more specific than `*`.
    for (const declaration of [
      'animation-duration',
      'animation-iteration-count',
      'transition-duration',
      'scroll-behavior',
    ]) {
      expect(block).toMatch(new RegExp(declaration + ':[^;]*!important'));
    }
  });

  it('emits the suppression last, after everything it has to defeat', () => {
    // Moved earlier, the block still says `!important` yet is beaten by any later rule of equal
    // specificity, with every other gate green.
    const chain = withoutComments(readFileSync(join(UI_ROOT, 'src/styles.scss'), 'utf8'));
    const imports = [...chain.matchAll(/@import\s+'([^']+)'/g)].map(([, name]) => name);

    expect(imports).toContain('motion');
    expect(imports.at(-1)).toBe('motion');
  });

  it('runs no animation whose keyframe is not among the three', () => {
    // `check-forbidden-apis.mjs` refuses a fourth definition; this refuses a use that names none
    // of the three, since a typo in an `animation` shorthand fails silently.
    const offenders: string[] = [];

    for (const file of scssFiles(join(UI_ROOT, 'src'))) {
      const source = withoutComments(readFileSync(file, 'utf8'));

      // `[;{}]` and the `m` flag, so a declaration after a nested rule's closing brace is
      // seen; `animation-name` too, which is the other spelling that names a keyframe.
      for (const match of source.matchAll(/(?:^|[;{}])\s*animation(?:-name)?:\s*([^;}]+)/gm)) {
        // Satisfies `noUncheckedIndexedAccess`; the group is not optional.
        const value = match[1]?.trim() ?? '';
        if (value === 'none' || value.startsWith('none')) {
          continue;
        }
        if (!KEYFRAMES.some((name) => new RegExp('\\b' + name + '\\b').test(value))) {
          offenders.push(`${relative(UI_ROOT, file)}: ${value}`);
        }
      }
    }

    expect(offenders).toEqual([]);
  });
});
