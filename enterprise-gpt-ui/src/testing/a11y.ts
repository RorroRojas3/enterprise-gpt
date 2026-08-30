import { page } from '@vitest/browser/context';
import axe, { ImpactValue, Result, RunOptions } from 'axe-core';
import { expect } from 'vitest';

// Re-exported so an audit spec needs one import; the list itself lives apart so the
// jsdom coverage guard can read it without pulling `axe-core` in with it.
export { AXE_ROUTES } from './a11y-routes';

/**
 * The axe-core harness behind US-1405's first acceptance criterion.
 *
 * **These specs run in a real browser, not jsdom**, through the `test-a11y` target in
 * `angular.json`. That is not a preference: half of what axe checks is computed style —
 * colour contrast, whether an element is visible, whether a target is large enough — and
 * jsdom has no cascade, no layout and no computed colour. A jsdom axe run reports those
 * rules as `incomplete` and passes, which reads as a clean bill of health and is not one.
 * It is also what makes "in both light and dark themes" mean anything: the themes differ
 * only in custom properties, which jsdom never resolves.
 *
 * The cost is that this target loads the application's real stylesheet through its
 * `buildTarget`, and that `setupFiles` is **empty** — `setup-dom.ts` installs fakes for
 * `matchMedia`, `IntersectionObserver` and `ResizeObserver` because jsdom implements
 * none, and in Chromium those would shadow the real implementations and answer `false`
 * to every media query.
 */

/**
 * Only these fail the build; `moderate` and `minor` are **discarded**, not reported.
 *
 * Stated plainly because the alternative reading — that they are surfaced somewhere —
 * would be a safety net that does not exist. `resultTypes: ['violations']` likewise means
 * axe populates no `incomplete` bucket, so a rule it genuinely could not decide is
 * silent. Both are deliberate: this is a gate, and a gate that prints advisories nobody
 * reads trains people to skim its output.
 */
const ENFORCED: readonly ImpactValue[] = ['serious', 'critical'];

/** The two themes every screen is checked in. `data-bs-theme` is Bootstrap 5.3's own. */
export const THEMES = ['light', 'dark'] as const;

export type Theme = (typeof THEMES)[number];

/**
 * Puts the document in one theme, exactly as `ThemeService` does at runtime.
 *
 * The attribute rather than the service, because what is under test is the rendered
 * result: a spec that drove the service would still pass if the service stopped writing
 * the attribute.
 */
export function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-bs-theme', theme);
}

export function clearTheme(): void {
  document.documentElement.removeAttribute('data-bs-theme');
}

/**
 * The three widths US-1403 defines, as a spec drives them.
 *
 * The target's `browserViewport` is 1280x900, which is the *desktop* case — and stating
 * it was necessary rather than cosmetic: Vitest's browser viewport defaults to 414x896,
 * so without it every audit in this suite ran at a phone width and the sidebar, the data
 * tables and the rail were never rendered, let alone audited.
 */
export const VIEWPORTS = {
  desktop: { width: 1280, height: 900 },
  tablet: { width: 900, height: 800 },
  mobile: { width: 390, height: 844 },
} as const;

export type ViewportName = keyof typeof VIEWPORTS;

/** Resizes the browser and waits for the resize to have been applied. */
export async function atViewport(name: ViewportName): Promise<void> {
  const { width, height } = VIEWPORTS[name];
  await page.viewport(width, height);
}

/**
 * US-1403's last criterion, which only a real browser can answer: nothing may overflow
 * the viewport horizontally.
 *
 * Measured on the scrolling element rather than on `document.body`, and that distinction
 * is the whole point — inside the signed-in app the shell is `height: 100dvh;
 * overflow: hidden`, so the body can never scroll in either axis and a body-level
 * assertion is vacuously true however badly a screen overflows. What this catches is
 * content wider than the box that is meant to contain it.
 */
export function expectNoHorizontalOverflow(root: HTMLElement, label: string): void {
  const offenders = [root, ...root.querySelectorAll<HTMLElement>('*')].filter((node) => {
    // A `visually-hidden` element is a clipped 1x1 box whose content is *meant* to
    // overflow it — that is how the pattern keeps text in the accessibility tree while
    // taking it off the screen. Every one of them would otherwise be reported, which is
    // how this check first ran: three false positives and nothing real.
    if (node.clientWidth <= 1) {
      return false;
    }

    const style = getComputedStyle(node);

    // An element that both declares the ellipsis pattern and is a pure text run is
    // *contained* — the pattern doing its job, pushing nothing sideways. `nowrap` is what
    // holds the line: `td` sets `overflow: hidden` and `text-overflow` but wraps, so a cell
    // clipping content it cannot ellipsise is still reported. This bounds containment only;
    // whether the surviving text is still worth reading is not a question of overflow.
    if (
      node.childElementCount === 0 &&
      style.textOverflow === 'ellipsis' &&
      style.whiteSpace === 'nowrap'
    ) {
      return false;
    }

    // Only a container that *clips* is a defect. `overflow-x: auto` or `scroll` is a
    // deliberate scroll region — the code block, the pill strip, a wide table — and
    // reporting those would be reporting the design.
    return node.scrollWidth > node.clientWidth + 1 && style.overflowX === 'hidden';
  });

  expect(
    offenders.map((node) => `${node.tagName.toLowerCase()}.${node.className}`),
    `content overflows its container horizontally on ${label}`,
  ).toEqual([]);
}

/**
 * Mounts a routed screen the way the signed-in shell does, so a layout fact measured
 * against it is the one a reader meets.
 *
 * `.shell__main` is a clipped flex column of definite height, and that is exactly the
 * context a screen's own scrolling depends on: appended to a bare `document.body` every
 * host simply grows and the page scrolls, so a screen that cannot scroll inside the
 * shell still passes. The class name is not reused — this stands in for the shell rather
 * than importing its stylesheet.
 */
export function mountLikeShell(element: HTMLElement): HTMLElement {
  const main = document.createElement('div');
  main.style.cssText = 'display:flex;flex-direction:column;height:100dvh;overflow:hidden';
  main.append(element);
  document.body.append(main);

  return main;
}

/**
 * Every row of a screen taller than its container is reachable by scrolling it.
 *
 * The failure this exists to catch is silent and total: a card that clips its own
 * overflow while shrinking inside a flex column leaves the host nothing to scroll, so the
 * rows past the fold cannot be reached at any width, by pointer, keyboard or otherwise.
 * Only a real browser can answer it, and only inside {@link mountLikeShell}.
 */
export function expectScrollsToItsLastRow(
  host: HTMLElement,
  lastRow: HTMLElement,
  label: string,
): void {
  expect(
    host.scrollHeight,
    `nothing scrolls on ${label}: content is clipped rather than reachable`,
  ).toBeGreaterThan(host.clientHeight);

  host.scrollTop = host.scrollHeight;

  expect(
    Math.round(lastRow.getBoundingClientRect().bottom),
    `the last row is unreachable on ${label} even scrolled to the end`,
  ).toBeLessThanOrEqual(Math.round(host.getBoundingClientRect().bottom) + 1);
}

function describeViolation(violation: Result): string {
  const where = violation.nodes
    .slice(0, 4)
    .map((node) => `      ${node.target.join(' ')}\n        ${node.failureSummary ?? ''}`)
    .join('\n');
  const more = violation.nodes.length > 4 ? `\n      …and ${violation.nodes.length - 4} more` : '';

  return `  [${violation.impact}] ${violation.id} — ${violation.help}\n${where}${more}\n      ${violation.helpUrl}`;
}

/**
 * Runs axe over `root` and fails with a readable report if anything serious or critical
 * survives.
 *
 * `region` is disabled: it wants every node inside a landmark, and these specs mount one
 * screen into a bare fixture host rather than the full application frame, so it would
 * fire on the fixture rather than on anything a user meets. The landmark structure the
 * rule exists to protect is the shell's, and `shell-responsive.spec.ts` asserts it
 * directly.
 */
export async function expectNoSeriousViolations(
  root: Element,
  label: string,
  options: RunOptions = {},
): Promise<void> {
  const { rules, ...rest } = options;
  const results = await axe.run(root, {
    resultTypes: ['violations'],
    // Merged, not spread over: `rules` is the likeliest thing a caller passes, and
    // `...options` would silently re-enable `region` for that caller alone.
    //
    // `target-size` is switched **on**, and it is the only rule this harness enables that
    // axe does not. It is WCAG 2.2 AA (SC 2.5.8) at `serious`, and axe-core ships it
    // disabled by default — so every icon-only control in this application has gone
    // unmeasured, which is the same shape of gap US-1405 found with `--brand` on
    // `--active-bg`: the run was green because nothing was looking. Switched on with
    // US-1103, whose thumbs are the first pair of adjacent icon-only toggles here, and
    // every other audit in this suite was already clean under it.
    //
    // It is also the rule most prone to answering `incomplete` — its own messages carry
    // "too many overlapping elements" and "obscured" — and `resultTypes` above drops that
    // bucket, so a target axe could not measure is silence rather than a failure. The
    // general warning is at the top of this file; it is worth naming the rule it bites.
    rules: { region: { enabled: false }, 'target-size': { enabled: true }, ...rules },
    ...rest,
  });

  // `impact` is typed nullable *and* optional by axe: a rule that produced no impact
  // rating is reported rather than enforced, which is the safe direction — an unrated
  // violation is not silently treated as critical.
  const enforced = results.violations.filter((violation) => {
    const impact = violation.impact;
    return impact !== null && impact !== undefined && ENFORCED.includes(impact);
  });

  if (enforced.length > 0) {
    const report = enforced.map(describeViolation).join('\n');
    expect.fail(
      `${enforced.length} serious or critical accessibility violation(s) on ${label}:\n${report}`,
    );
  }
}
