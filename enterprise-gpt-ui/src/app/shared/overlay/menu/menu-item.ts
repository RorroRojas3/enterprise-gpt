import { Directive } from '@angular/core';

/**
 * One row inside {@link Menu}. Apply it to a `<button>` or an `<a>` — never a `<div>`
 * or an `<i>`, which is the pattern the design prototypes use and the PRD's
 * accessibility precedence rule says to correct.
 *
 * `tabindex="-1"` is what makes the menu's roving focus work: Tab leaves the menu
 * entirely, and the arrow keys move within it.
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
