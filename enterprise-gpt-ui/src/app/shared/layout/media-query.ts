import { DOCUMENT, DestroyRef, Signal, inject, signal } from '@angular/core';

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
