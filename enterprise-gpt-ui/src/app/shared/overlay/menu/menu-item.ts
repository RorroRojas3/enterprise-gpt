import { Directive } from '@angular/core';

/**
 * A {@link Menu} row: apply to a `<button>` or `<a>`, never a `<div>`. The host `role` yields to
 * a template attribute, which is how pickers widen rows to `menuitemradio`/`menuitemcheckbox`
 * with their own `aria-checked`; roving focus keys on `appMenuItem`, so those rows still rove.
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
