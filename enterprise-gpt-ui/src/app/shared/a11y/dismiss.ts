export interface DismissHandlers {
  /** Escape pressed inside the overlay. */
  readonly onEscape?: () => void;
  /** A pointer went down outside the overlay. */
  readonly onOutside?: () => void;
  /** An ancestor scrolled. Menus close rather than reposition. */
  readonly onScroll?: () => void;
  /**
   * An element outside the overlay that still counts as *inside* it for the
   * outside-press test.
   *
   * For a trigger nested in the overlay's own host — every `Menu` — this is not needed.
   * It exists for a trigger that sits elsewhere and toggles the overlay itself, like
   * US-307's composer pill: without it, the press closes the panel on `pointerdown` and
   * the trigger's own `click` reopens it, so the control can never be used to dismiss.
   *
   * A callback rather than an element, because the trigger may not exist when the
   * listener is registered.
   */
  readonly insideAlso?: () => HTMLElement | null;
}

/**
 * Wires the three ways an overlay gets dismissed, with one place deciding how each
 * behaves.
 *
 * Escape is listened for on the overlay rather than on `document`, so a menu opened
 * inside a modal closes only the menu. Outside clicks use `pointerdown` and
 * `composedPath()` so a press that starts inside and ends outside does not dismiss,
 * and so a click inside a shadow tree still counts as inside.
 *
 * Every listener is registered against one `AbortSignal`; aborting it removes all of
 * them, which is why callers pass the signal from a controller aborted in
 * `DestroyRef.onDestroy`.
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
