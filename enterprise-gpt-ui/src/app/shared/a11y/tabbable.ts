const TABBABLE_SELECTOR = [
  'a[href]',
  'button',
  'input',
  'select',
  'textarea',
  '[tabindex]',
  '[contenteditable="true"]',
].join(',');

/**
 * The elements inside `root` that keyboard focus can reach, in DOM order.
 *
 * Deliberately does not consult `getComputedStyle`: jsdom performs no layout, so a
 * visibility check would be a lie in every unit test and a per-element style read in
 * the browser. Overlays here hide by unmounting rather than by `visibility`, so there
 * is nothing for it to catch.
 */
export function tabbableWithin(root: Element): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(TABBABLE_SELECTOR)].filter(isTabbable);
}

function isTabbable(element: HTMLElement): boolean {
  if (element.hasAttribute('disabled') || element.hasAttribute('hidden')) {
    return false;
  }
  if (element.getAttribute('tabindex') === '-1') {
    return false;
  }
  // `inert` and `aria-hidden` remove a whole subtree, not just the element carrying
  // them, so both have to be checked against every ancestor.
  return element.closest('[inert]') === null && element.closest('[aria-hidden="true"]') === null;
}
