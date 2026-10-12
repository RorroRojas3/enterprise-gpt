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
 * The elements inside `root` that keyboard focus can reach, in DOM order. No computed-style
 * check: jsdom has no layout, and overlays here hide by unmounting.
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
