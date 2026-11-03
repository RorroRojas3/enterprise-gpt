import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { recoverFocusOn } from '@core/a11y/recover-focus';
import { ToastItem } from './toast-item';
import { ToastStore } from './toast-store';

/**
 * Mounted in `App`, not `Shell`, so chrome-free routes get toasts too. Both live regions exist
 * before the first toast, since a live region created with its content is not announced.
 */
@Component({
  selector: 'app-toast-region',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ToastItem],
  templateUrl: './toast-region.html',
})
export class ToastRegion {
  protected readonly store = inject(ToastStore);

  constructor() {
    // On the list, not the buttons: replacement, the cap and the timer remove toasts too.
    recoverFocusOn(this.store.toasts);
  }
}
