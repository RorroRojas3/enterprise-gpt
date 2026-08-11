import { ElementRef, Signal, afterNextRender, inject, Injector } from '@angular/core';

/**
 * Moves focus to an element once, after the view first renders.
 *
 * Every full-page auth state is reached by a router navigation or a guard redirect,
 * and Angular moves focus for neither. Without this, a keyboard user resumes tabbing
 * from the top of the document and a screen-reader user is told nothing at all — and
 * in the `/admin` → `/forbidden` case the element that *had* focus is removed from
 * the DOM outright, which drops focus onto `<body>`.
 *
 * Focusing the heading rather than announcing through a live region is deliberate,
 * and it is the pattern `core/config/fatal-shell.ts` already uses: a region inserted
 * together with its text announces nothing, because `aria-live` reports *changes* to
 * a region that was already in the accessibility tree.
 *
 * Must be called from an injection context.
 *
 * @param target The element to focus. It needs `tabindex="-1"` to be focusable, and it
 *   must sit outside any control flow — a `viewChild.required` that resolves to
 *   nothing throws inside the render hook, where Angular routes the error to the
 *   `ErrorHandler` and the page simply loses its focus move without failing. A target
 *   that can legitimately be absent belongs to whichever component owns it; see
 *   `ErrorPanel.focusHeading`.
 */
export function focusOnRender(target: Signal<ElementRef<HTMLElement>>): void {
  const injector = inject(Injector);

  afterNextRender(() => target().nativeElement.focus(), { injector });
}
