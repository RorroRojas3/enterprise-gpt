import { Directive } from '@angular/core';

/**
 * Projected content replacing the {@link Menu} trigger's default icon. The button stays
 * Menu-owned, and `label` becomes its `aria-label` so visible text names the subject too.
 */
@Directive({
  selector: '[appMenuTriggerContent]',
})
export class MenuTriggerContent {}
