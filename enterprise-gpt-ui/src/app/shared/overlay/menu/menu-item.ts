import { Directive } from '@angular/core';

/**
 * One row inside {@link Menu}. Apply it to a `<button>` or an `<a>` — never a `<div>`
 * or an `<i>`, which is the pattern the design prototypes use and the PRD's
 * accessibility precedence rule says to correct.
 *
 * `tabindex="-1"` is what makes the menu's roving focus work: Tab leaves the menu
 * entirely, and the arrow keys move within it.
 *
 * The host `role` is a default: a static host attribute yields to one written in
 * the consumer's template, which is how the pickers' rows widen to
 * `role="menuitemradio"` / `role="menuitemcheckbox"` — binding their own
 * `[attr.aria-checked]` beside it, where the template linter can verify the
 * pairing. (A real `<input type="checkbox">` is not a valid child of
 * `role="menu"`.) Roving focus keys on the `appMenuItem` attribute, so widened
 * rows rove identically.
 */
@Directive({
  selector: '[appMenuItem]',
  host: {
    role: 'menuitem',
    tabindex: '-1',
    class: 'menu__item',
  },
})
export class MenuItem {}
