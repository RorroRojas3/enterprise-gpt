import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { USAGE_RANGES, UsageRange } from './admin-reports-route';

/**
 * The 7/30/90-day range control, frame `5j`'s segmented pill group.
 *
 * The board draws three `<span>`s. These are real `<button>`s in a `role="group"` carrying
 * `aria-pressed`, which is `design-system.md` §9's standing correction against the prototypes: a
 * control that changes what the page shows has to be reachable, pressable and announced as the
 * toggle it is.
 *
 * `aria-pressed` rather than a radio group, because each button is a toggle that is either the
 * active span or not — and a radio group would put the three on one tab stop, which is the right
 * shape for a form field and the wrong one for a toolbar of three visible choices.
 *
 * **There is no `busy` input.** A control that goes inert while its own request is in flight blurs
 * itself, which drops a keyboard reader back to the top of the document — and the store's
 * `switchMap` already discards the superseded request, so the disable bought nothing.
 */
@Component({
  selector: 'app-range-segmented',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './range-segmented.html',
  styleUrl: './range-segmented.scss',
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
