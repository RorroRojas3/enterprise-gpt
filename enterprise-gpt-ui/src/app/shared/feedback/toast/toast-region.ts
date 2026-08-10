import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { ToastStore } from '@core/notifications/toast-store';
import { ToastItem } from './toast-item';

/**
 * The fixed toast stack. Mounted **once**, in the app shell, beside the router outlet.
 *
 * Two sibling live regions rather than one, because a region cannot be both polite and
 * assertive: success and info must not interrupt, while an error must. Both have to
 * exist in the DOM before the first toast arrives — a live region created at the same
 * time as its content is not announced — which is the other reason this is mounted
 * once at the shell rather than per route.
 */
@Component({
  selector: 'app-toast-region',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ToastItem],
  templateUrl: './toast-region.html',
  styleUrl: './toast-region.scss',
})
export class ToastRegion {
  protected readonly store = inject(ToastStore);

  /** The toast id whose retry affordance was used. */
  readonly retried = output<string>();
}
