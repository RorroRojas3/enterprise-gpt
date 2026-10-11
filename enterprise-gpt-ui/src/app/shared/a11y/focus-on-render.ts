import { ElementRef, Signal, afterNextRender, inject, Injector } from '@angular/core';

/**
 * Moves focus to an element once, after the view first renders, since Angular moves none on a
 * navigation or redirect. Focus rather than a live region, which announces nothing when inserted
 * with its text.
 *
 * @param target Needs `tabindex="-1"`, and must sit outside control flow: a missing
 *   `viewChild.required` throws in the render hook, silently losing the focus move.
 */
export function focusOnRender(target: Signal<ElementRef<HTMLElement>>): void {
  const injector = inject(Injector);

  afterNextRender(() => target().nativeElement.focus(), { injector });
}
