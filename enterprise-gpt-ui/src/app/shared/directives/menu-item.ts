import { Directive } from '@angular/core';

/**
 * A {@link Menu} row: apply to a `<button>` or `<a>`, never a `<div>`. The host `role` yields to
 * a template attribute, which is how pickers widen rows to `menuitemradio`/`menuitemcheckbox`
 * with their own `aria-checked`; roving focus keys on `appMenuItem`, so those rows still rove.
 * Disable with `aria-disabled`, never the native attribute: a disabled button cannot take focus,
 * which breaks roving focus and leaves Escape nowhere to fire from.
 */
@Directive({
  selector: '[appMenuItem]',
  host: {
    role: 'menuitem',
    tabindex: '-1',
    // `text-wrap`: `.dropdown-item` is nowrap, and a long label would spill out of the panel.
    class: 'dropdown-item d-flex align-items-center gap-2 rounded-2 text-wrap',
  },
})
export class MenuItem {}
