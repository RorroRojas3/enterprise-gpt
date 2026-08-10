import { Directive } from '@angular/core';

/** A rule between groups of {@link MenuItem}s. Apply it to an `<hr>`. */
@Directive({
  selector: '[appMenuSeparator]',
  host: {
    role: 'separator',
    class: 'menu__separator',
  },
})
export class MenuSeparator {}
