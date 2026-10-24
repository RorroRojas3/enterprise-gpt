import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

/**
 * Never takes focus on appearing, since the user is still ticking checkboxes; a polite live
 * region announces the count instead.
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
