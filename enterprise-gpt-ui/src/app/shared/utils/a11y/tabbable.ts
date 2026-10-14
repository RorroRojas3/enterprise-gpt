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

/**
 * Moves focus into a dialog just opened: `[data-modal-autofocus]`, else the first tabbable
 * content control, else the heading. The close button is skipped, or every dialog would open on
 * it. Not `autofocus`, which means "on document load" and is lint-banned.
 */
export function focusInitialTarget(dialog: HTMLElement): void {
  const target =
    dialog.querySelector<HTMLElement>('[data-modal-autofocus]') ??
    tabbableWithin(dialog).find((element) => !element.hasAttribute('data-modal-dismiss')) ??
    // The heading carries tabindex="-1" so it can be the last resort: a dialog whose focus lands
    // on <body> is one the screen reader never announces.
    dialog.querySelector<HTMLElement>('h2');
  target?.focus();
}
