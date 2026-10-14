import { page } from '@vitest/browser/context';
import axe, { ImpactValue, Result, RunOptions } from 'axe-core';
import { expect } from 'vitest';

// Re-exported so an audit spec needs one import; the list itself lives apart so the
// jsdom coverage guard can read it without pulling `axe-core` in with it.
export { AXE_ROUTES } from './a11y-routes';

/**
 * Runs in a real browser (`test-a11y`): axe needs computed style and resolved custom properties,
 * which jsdom lacks. `setupFiles` is empty because `setup-dom.ts`'s fakes would shadow Chromium's
 * real `matchMedia` and observers.
 */

/**
 * Only these fail the build. `moderate`, `minor` and `incomplete` results are discarded, not
 * reported: a gate that prints advisories trains people to skim it.
 */
const ENFORCED: readonly ImpactValue[] = ['serious', 'critical'];

/** The two themes every screen is checked in. */
export const THEMES = ['light', 'dark'] as const;

export type Theme = (typeof THEMES)[number];

/** Sets the attribute directly: a spec driving `ThemeService` would pass if it stopped writing. */
export function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-bs-theme', theme);
}

export function clearTheme(): void {
  document.documentElement.removeAttribute('data-bs-theme');
}

/**
 * The app's three layout widths, plus desktop at its 992px edge: the persistent sidebar leaves the
 * least room for content there. The target sets `browserViewport` to desktop because Vitest
 * defaults to 414x896, a phone width.
 */
export const VIEWPORTS = {
  desktop: { width: 1280, height: 900 },
  desktopMin: { width: 992, height: 800 },
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
 * Nothing may overflow horizontally. Checked per element, not on `body`, which the shell's
 * `overflow: hidden` keeps from ever scrolling.
 */
export function expectNoHorizontalOverflow(root: HTMLElement, label: string): void {
  const offenders = [root, ...root.querySelectorAll<HTMLElement>('*')].filter((node) => {
    // `visually-hidden` boxes are 1x1 and overflow by design.
    if (node.clientWidth <= 1) {
      return false;
    }

    const style = getComputedStyle(node);

    // A nowrap ellipsis text run is contained. A wrapping `td` with `text-overflow` can still
    // clip, so it is still reported.
    if (
      node.childElementCount === 0 &&
      style.textOverflow === 'ellipsis' &&
      style.whiteSpace === 'nowrap'
    ) {
      return false;
    }

    // Only clipping is a defect; `auto` and `scroll` are deliberate scroll regions.
    return node.scrollWidth > node.clientWidth + 1 && style.overflowX === 'hidden';
  });

  expect(
    offenders.map((node) => `${node.tagName.toLowerCase()}.${node.className}`),
    `content overflows its container horizontally on ${label}`,
  ).toEqual([]);
}

/**
 * Mounts a screen in a clipped flex column of definite height, as the shell's `<main>` does: on a
 * bare `body` the host just grows, so a screen that cannot scroll in the shell would still pass.
 */
export function mountLikeShell(element: HTMLElement): HTMLElement {
  const main = document.createElement('div');
  main.style.cssText = 'display:flex;flex-direction:column;height:100dvh;overflow:hidden';
  main.append(element);
  document.body.append(main);

  return main;
}

/**
 * The last row is reachable by scrolling; a card clipping its own overflow in a flex column
 * leaves nothing to scroll. Use inside {@link mountLikeShell}.
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
 * Fails on any serious or critical axe violation. `region` is off because specs mount a screen
 * without the shell's landmarks, which `shell-responsive.spec.ts` asserts directly.
 */
export async function expectNoSeriousViolations(
  root: Element,
  label: string,
  options: RunOptions = {},
): Promise<void> {
  const { rules, ...rest } = options;
  const results = await axe.run(root, {
    resultTypes: ['violations'],
    // Merged so a caller's `rules` cannot re-enable `region`. `target-size` (WCAG 2.2 SC 2.5.8)
    // is on though axe ships it off; it often answers `incomplete`, which is dropped above.
    rules: { region: { enabled: false }, 'target-size': { enabled: true }, ...rules },
    ...rest,
  });

  // axe types `impact` as nullable and optional; an unrated violation is not treated as critical.
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
