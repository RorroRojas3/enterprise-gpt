import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { AXE_ROUTES } from './a11y-routes';

/**
 * That the axe run actually covers every route it claims to.
 *
 * `AXE_ROUTES` is a hand-written list, and the way such a list rots is a *new* route:
 * added to the router, never added here, audited by nothing, with every gate green. That
 * is the same failure `check-tokens.mjs` guards against for painted surfaces, and this is
 * the same shape of guard.
 *
 * **It runs in jsdom, not in the browser target**, and deliberately: it reads the spec
 * files off disk, which `node:fs` can do here and cannot do in Chromium. It asserts the
 * coverage rather than performing it — the audits themselves are the `*.a11y.spec.ts`
 * files this one reads. It imports the route list from `a11y-routes.ts` rather than from
 * `a11y.ts` for the same reason: that module pulls in `axe-core`, which this suite has no
 * use for.
 */

/** `import.meta.dirname` is `src/testing`, so one level up is `src` itself. */
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
 * Whether some audit actually *navigates* to a route.
 *
 * Matching the raw file text would be satisfied by a doc comment, a `provideRouter`
 * table or a commented-out test — so deleting an `it` block while leaving the sentence
 * above it would keep this green, which is exactly the rot the guard exists to catch.
 * Requiring the route to be an argument to `open(…)` or `navigateByUrl(…)` is a much
 * narrower claim.
 *
 * The boundary after the route matters too: without it `/chat` would be satisfied by
 * `/chatter`. A quote or backtick ends the literal; a `/` or a `${` continues it, which
 * is how `/chat/${conversation.id}` counts — both URLs are the same route through one
 * `UrlMatcher`.
 */
function navigatesTo(route: string): RegExp {
  const escaped = route.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  return new RegExp(
    String.raw`(?:open|navigateByUrl)\(\s*['\`]` + escaped + String.raw`(?:['\`/]|\$\{)`,
  );
}

describe('axe coverage (US-1405)', () => {
  const sources = a11ySpecs(SRC);

  it('has at least one audit to be a coverage record of', () => {
    expect(sources.length).toBeGreaterThan(0);
  });

  it.each([...AXE_ROUTES])('navigates to %s in an audit', (route) => {
    const navigation = navigatesTo(route);

    expect(sources.some((source) => navigation.test(source))).toBe(true);
  });

  it('states the documents route as missing rather than covering it', () => {
    // US-1405's criterion names four route groups and only three exist. This asserts the
    // absence *and* its reason, so US-1002 finds a failing test rather than a silent gap
    // when it adds the route: it will have to add the entry here in the same commit.
    expect(AXE_ROUTES).not.toContain('/documents');
  });
});
