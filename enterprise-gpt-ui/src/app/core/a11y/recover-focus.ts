import {
  DOCUMENT,
  ElementRef,
  Injector,
  Signal,
  afterNextRender,
  effect,
  inject,
} from '@angular/core';

/** Moves focus to `<main>` when removing the focused control dropped it; other focus is left alone. */
export function recoverFocus(document: Document): void {
  const active = document.activeElement;

  if (active === null || active === document.body || !active.isConnected) {
    document.getElementById('main-content')?.focus();
  }
}

/**
 * Recovers focus for a component whose `source` can remove the control holding it. An effect runs
 * before the template updates, the last moment focus is visible inside what is about to leave.
 */
export function recoverFocusOn(source: Signal<unknown>): void {
  const document = inject(DOCUMENT);
  const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  const injector = inject(Injector);

  effect(() => {
    source();

    if (host.contains(document.activeElement)) {
      afterNextRender(() => recoverFocus(document), { injector });
    }
  });
}
