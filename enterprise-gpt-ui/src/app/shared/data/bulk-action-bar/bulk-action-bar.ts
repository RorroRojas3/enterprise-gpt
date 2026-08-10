import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

/**
 * The floating bar that appears once rows are selected — frame `4a`.
 *
 * It must **not** take focus when it appears: the user is still working through the
 * checkboxes, and moving focus out of the list would undo that. The count sits in a
 * polite live region instead, which is how a screen-reader user learns it is there.
 *
 * `note` carries the honest caveat where one applies — "Select-all covers the 24
 * loaded rows only".
 */
@Component({
  selector: 'app-bulk-action-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './bulk-action-bar.html',
  styleUrl: './bulk-action-bar.scss',
})
export class BulkActionBar {
  readonly count = input.required<number>();
  readonly note = input<string>('');

  readonly cleared = output<void>();

  protected readonly summary = computed(() =>
    this.count() === 1 ? '1 selected' : `${this.count()} selected`,
  );
}
