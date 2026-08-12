import { Directive } from '@angular/core';

/**
 * Marks projected content as the {@link Menu} trigger's face, replacing the
 * default icon button — the model and tools pills project their own markup
 * this way. The button itself stays Menu-owned, so `aria-haspopup`,
 * `aria-expanded`, and the keyboard behaviors are identical for every
 * consumer; `label` moves onto the button as `aria-label`, because projected
 * visible text would otherwise name the menu without naming its subject.
 */
@Directive({
  selector: '[appMenuTriggerContent]',
})
export class MenuTriggerContent {}
