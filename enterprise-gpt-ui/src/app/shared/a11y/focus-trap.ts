import { tabbableWithin } from './tabbable';

/**
 * Cycles Tab and Shift+Tab within `root`.
 *
 * Only the menu and the jsdom fallback path need this. A modal or an offcanvas opened
 * with `showModal()` is in the top layer, where the browser inerts everything behind
 * it — real inertness that no `keydown` handler can match.
 *
 * @returns A function that stops trapping.
 */
export function trapWithin(root: HTMLElement): () => void {
  const onKeydown = (event: KeyboardEvent): void => {
    if (event.key !== 'Tab') {
      return;
    }

    const tabbable = tabbableWithin(root);
    if (tabbable.length === 0) {
      // Nothing to move to; keeping focus where it is beats letting it escape.
      event.preventDefault();
      return;
    }

    const first = tabbable[0];
    const last = tabbable[tabbable.length - 1];
    const active = root.ownerDocument.activeElement;

    if (event.shiftKey && (active === first || !root.contains(active))) {
      event.preventDefault();
      last?.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first?.focus();
    }
  };

  root.addEventListener('keydown', onKeydown);
  return () => root.removeEventListener('keydown', onKeydown);
}
