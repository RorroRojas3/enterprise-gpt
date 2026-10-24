import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { USAGE_RANGES, UsageRange } from './admin-reports-route';

/**
 * `aria-pressed` buttons in a `role="group"`, not a radio group, which would put three visible
 * choices on one tab stop. No `busy` input: going inert would blur the control and drop keyboard
 * focus, and the store's `switchMap` already discards superseded requests.
 */
@Component({
  selector: 'app-range-segmented',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './range-segmented.html',
})
export class RangeSegmented {
  readonly value = input.required<UsageRange>();

  readonly selected = output<UsageRange>();

  // A field, not a template expression: `provideCheckNoChangesConfig({ exhaustive: true })` fails
  // any array allocated during change detection, because it is a new value every pass.
  protected readonly ranges = USAGE_RANGES;

  protected onSelect(range: UsageRange): void {
    if (range !== this.value()) {
      this.selected.emit(range);
    }
  }
}
