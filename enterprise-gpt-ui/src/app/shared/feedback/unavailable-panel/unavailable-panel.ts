import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Ridgeline } from '@shared/ridgeline/ridgeline';

let nextId = 0;

/**
 * A capability this deployment does not have — frames `4i` and `5i`.
 *
 * Deliberately a different component from `ErrorPanel`, and the type signature is what
 * enforces the difference: there is **no** `retry` output, no action slot and no
 * `error` input, because nothing here is worth retrying. A missing API does not come
 * back because the user pressed a button.
 *
 * It renders no rows, no records and no placeholder values. FR-49 exists because the
 * old client's habit of filling an empty screen with sample data made "we have not
 * built this" indistinguishable from "your data is empty".
 *
 * Named `UnavailablePanel`, not `UnavailablePanelComponent`: this workspace uses the
 * v21 suffix-free convention throughout, and one inconsistently named class would be
 * worse than the literal wording of the acceptance criterion.
 */
@Component({
  selector: 'app-unavailable-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Ridgeline],
  templateUrl: './unavailable-panel.html',
  styleUrl: './unavailable-panel.scss',
})
export class UnavailablePanel {
  /** Names the capability: "Your documents aren't available yet". */
  readonly heading = input.required<string>();

  /** Says which API is missing and what still works without it. */
  readonly message = input.required<string>();

  protected readonly headingId = `unavailable-heading-${nextId++}`;
}
