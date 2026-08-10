import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TOAST_ICONS, Toast } from '@core/notifications/toast.model';
import { Icon } from '@shared/icon/icon';
import { IconName } from '@shared/icon/icon-names';

/** One toast. Layout only — {@link ToastRegion} owns the live regions and the queue. */
@Component({
  selector: 'app-toast-item',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './toast-item.html',
  styleUrl: './toast-item.scss',
})
export class ToastItem {
  readonly toast = input.required<Toast>();

  readonly dismissed = output<string>();
  readonly retried = output<string>();

  protected readonly icon = computed<IconName>(() => TOAST_ICONS[this.toast().tone] as IconName);
  protected readonly dismissLabel = computed(() => `Dismiss: ${this.toast().title}`);
}
