import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { AXE_ROUTES } from './a11y-routes';

/**
 * Checks every `AXE_ROUTES` entry is audited. Runs in jsdom because it reads the specs with
 * `node:fs`, and imports `a11y-routes.ts` so `axe-core` never loads.
 */

const SRC = resolve(import.meta.dirname, '..');

function a11ySpecs(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) {
      return a11ySpecs(path);
    }
    return path.endsWith('.a11y.spec.ts') ? [readFileSync(path, 'utf8')] : [];
  });
}

/**
 * Whether an audit passes the route to `open(…)` or `navigateByUrl(…)`, since raw text would
 * match a comment. The boundary rejects `/chatter` but accepts `/chat/${id}`, the same route.
 */
function navigatesTo(route: string): RegExp {
  const escaped = route.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  return new RegExp(
    String.raw`(?:open|navigateByUrl)\(\s*['\`]` + escaped + String.raw`(?:['\`/]|\$\{)`,
  );
}

describe('axe coverage', () => {
  const sources = a11ySpecs(SRC);

  it('has at least one audit to be a coverage record of', () => {
    expect(sources.length).toBeGreaterThan(0);
  });

  it.each([...AXE_ROUTES])('navigates to %s in an audit', (route) => {
    const navigation = navigatesTo(route);

    expect(sources.some((source) => navigation.test(source))).toBe(true);
  });
});
