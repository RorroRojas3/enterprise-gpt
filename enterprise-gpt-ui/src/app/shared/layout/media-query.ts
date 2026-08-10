import { DOCUMENT, DestroyRef, Signal, inject, signal } from '@angular/core';

/**
 * A media query as a signal.
 *
 * The `change` listener writes a signal, which is the zoneless-correct way to notify
 * Angular of something that happened outside its own event handling — no
 * `ApplicationRef.tick()`, no zone patch.
 *
 * Must be called in an injection context.
 *
 * @param query A media query string, e.g. `(max-width: 767px)`.
 */
export function injectMediaQuery(query: string): Signal<boolean> {
  const document = inject(DOCUMENT);
  const destroyRef = inject(DestroyRef);
  const matches = signal(false);

  const list = document.defaultView?.matchMedia?.(query);
  if (list) {
    matches.set(list.matches);
    const onChange = (event: MediaQueryListEvent): void => matches.set(event.matches);
    list.addEventListener('change', onChange);
    destroyRef.onDestroy(() => list.removeEventListener('change', onChange));
  }

  return matches.asReadonly();
}
