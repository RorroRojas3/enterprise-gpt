import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * The reduced-motion contract, US-1404.
 *
 * **jsdom applies no stylesheet, so no test in this repository can observe that an
 * animation stopped.** Rendering a component and reading `getComputedStyle` returns the
 * inline value and nothing else; there is no cascade, no media query evaluation and no
 * animation clock. Asserting suppression at the DOM is therefore not merely hard here,
 * it is impossible — which is why the visual half of this story is a `/ui-kit` check
 * with the OS preference switched on, and why what is pinned here is the *source*.
 *
 * Two of the three assertions below cover invariants nothing else does. `npm run lint`
 * refuses a fifth keyframe (`check-forbidden-apis.mjs`) and holds the four to the design
 * bundle (`check-tokens.mjs`); neither can see that the suppression block still exists,
 * that it still carries `!important`, or that it is still emitted last — and the last of
 * those is the one the entire contract rests on.
 */

const UI_ROOT = resolve(import.meta.dirname, '../..');
const STYLES = join(UI_ROOT, 'src/styles');

/** The four the design bundle defines, and the only four the app may run. */
const KEYFRAMES = ['blink', 'spin', 'ringpulse', 'ridgedash'];

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

describe('the reduced-motion contract (US-1404)', () => {
  it('suppresses every animation and transition under the preference', () => {
    const motion = withoutComments(readFileSync(join(STYLES, '_motion.scss'), 'utf8'));
    const at = motion.indexOf('@media (prefers-reduced-motion: reduce)');

    // Guarded rather than sliced blind: `slice(-1)` on a missing block yields a
    // one-character string, so the absence would surface as a puzzling `toContain`
    // failure instead of the thing that is actually wrong.
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
    // The whole contract rests on this line's position. `_motion.scss` says so, and
    // until now nothing checked it: moved above `kit` or `bootstrap-overrides`, the
    // block still exists, still says `!important`, and is still beaten by any later
    // rule of equal specificity — a silent regression with every other gate green.
    const chain = withoutComments(readFileSync(join(UI_ROOT, 'src/styles.scss'), 'utf8'));
    const imports = [...chain.matchAll(/@import\s+'([^']+)'/g)].map(([, name]) => name);

    expect(imports).toContain('motion');
    expect(imports.at(-1)).toBe('motion');
  });

  it('runs no animation whose keyframe the design bundle does not define', () => {
    // `check-forbidden-apis.mjs` refuses a fifth *definition*; this refuses a *use* that
    // names nothing — a typo in an `animation` shorthand is silent in CSS, and the
    // element simply never moves.
    const offenders: string[] = [];

    for (const file of scssFiles(join(UI_ROOT, 'src'))) {
      const source = withoutComments(readFileSync(file, 'utf8'));

      // `[;{}]` and the `m` flag, so a declaration after a nested rule's closing brace is
      // seen; `animation-name` too, which is the other spelling that names a keyframe.
      for (const match of source.matchAll(/(?:^|[;{}])\s*animation(?:-name)?:\s*([^;}]+)/gm)) {
        // `noUncheckedIndexedAccess` types a capture group as possibly undefined, and
        // the group is not optional — so this is a type guard, not a real branch.
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
