export interface DismissHandlers {
  /** Escape pressed inside the overlay. */
  readonly onEscape?: () => void;
  /** A pointer went down outside the overlay. */
  readonly onOutside?: () => void;
  /** An ancestor scrolled. Menus close rather than reposition. */
  readonly onScroll?: () => void;
  /**
   * A trigger outside the overlay that toggles it, counted as inside so its press doesn't close
   * the panel for its own `click` to reopen. A callback, as it may not exist at registration.
   */
  readonly insideAlso?: () => HTMLElement | null;
}

/**
 * Escape listens on the overlay, not `document`, so a menu inside a modal closes alone. Outside
 * presses use `pointerdown` and `composedPath()`, so drags out and shadow-tree clicks don't count.
 */
export function onDismiss(
  element: HTMLElement,
  handlers: DismissHandlers,
  signal: AbortSignal,
): void {
  const document = element.ownerDocument;

  if (handlers.onEscape) {
    element.addEventListener(
      'keydown',
      (event) => {
        if (event.key === 'Escape') {
          event.stopPropagation();
          handlers.onEscape?.();
        }
      },
      { signal },
    );
  }

  if (handlers.onOutside) {
    document.addEventListener(
      'pointerdown',
      (event) => {
        const path = event.composedPath();
        const alsoInside = handlers.insideAlso?.() ?? null;
        if (!path.includes(element) && !(alsoInside !== null && path.includes(alsoInside))) {
          handlers.onOutside?.();
        }
      },
      // Capture, so a handler that stops propagation on its own container cannot
      // leave the overlay stuck open.
      { capture: true, signal },
    );
  }

  if (handlers.onScroll) {
    document.addEventListener('scroll', () => handlers.onScroll?.(), {
      // Capture, because scroll does not bubble from the element that scrolled.
      capture: true,
      passive: true,
      signal,
    });
  }
}
