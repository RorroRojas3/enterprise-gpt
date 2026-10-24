import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Ridgeline } from '@shared/components/ridgeline/ridgeline';

let nextId = 0;

/**
 * A capability this deployment lacks, so unlike `ErrorPanel` it has no retry, action or error
 * input. No sample data either: that would read as "your data is empty".
 */
@Component({
  selector: 'app-unavailable-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Ridgeline],
  host: { class: 'd-block' },
  templateUrl: './unavailable-panel.html',
})
export class UnavailablePanel {
  /** Names the capability: "Your documents aren't available yet". */
  readonly heading = input.required<string>();

  /** Says which API is missing and what still works without it. */
  readonly message = input.required<string>();

  protected readonly headingId = `unavailable-heading-${nextId++}`;
}
