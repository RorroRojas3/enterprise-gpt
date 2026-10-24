import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Icon } from '@shared/icons/icon';
import { IconName } from '@shared/icons/icon-names';
import { TOAST_ICONS, Toast, ToastTone } from './toast.model';

const THEME_COLORS = {
  success: 'success',
  error: 'danger',
  warning: 'warning',
  info: 'info',
} as const satisfies Record<ToastTone, string>;

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
  private readonly color = computed(() => THEME_COLORS[this.toast().tone]);
  protected readonly accent = computed(() => `var(--bs-${this.color()})`);
  /** Emphasis, not the base colour: light info measures 2.74:1, under an icon's 3:1. */
  protected readonly iconClass = computed(() => `flex-shrink-0 mt-1 text-${this.color()}-emphasis`);
  protected readonly dismissLabel = computed(() => `Dismiss: ${this.toast().title}`);
}
