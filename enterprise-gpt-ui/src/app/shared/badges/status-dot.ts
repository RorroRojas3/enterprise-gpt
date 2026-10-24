import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type StatusTone =
  | 'success'
  | 'warning'
  | 'danger'
  | 'secondary'
  | 'info'
  | 'primary'
  | 'provider-azure-openai'
  | 'provider-bedrock'
  | 'provider-anthropic';

/**
 * A coloured dot with its meaning beside it. Colour is never the only channel (WCAG 1.4.1):
 * `labelHidden` makes the label screen-reader-only rather than removing it.
 */
@Component({
  selector: 'app-status-dot',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'd-inline-flex align-items-center gap-2 min-w-0 mw-100 small' },
  templateUrl: './status-dot.html',
})
export class StatusDot {
  readonly tone = input<StatusTone>('secondary');
  readonly label = input.required<string>();
  readonly labelHidden = input<boolean>(false);
  readonly size = input<number>(7);

  // Provider hues are app tokens with no `bg-*` utility.
  private readonly _isProvider = computed(() => this.tone().startsWith('provider-'));
  protected readonly markClass = computed(() => (this._isProvider() ? '' : `bg-${this.tone()}`));
  protected readonly markColor = computed(() =>
    this._isProvider() ? `var(--${this.tone()})` : null,
  );
}
