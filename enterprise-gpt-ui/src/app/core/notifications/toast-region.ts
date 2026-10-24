import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
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

  /** The toast id whose retry affordance was used. */
  readonly retried = output<string>();
}
