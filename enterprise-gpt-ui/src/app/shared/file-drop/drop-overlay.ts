import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Icon } from '@shared/icons/icon';

/**
 * The dashed affordance shown while a file drag is over a zone; separate from
 * {@link FileDropTarget}, which owns state. `aria-hidden` because a drag has no keyboard
 * equivalent: the attach button is the accessible path.
 */
@Component({
  selector: 'app-drop-overlay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  // `pe-none`: the pointer is mid-drag over the zone beneath, and an overlay that took
  // hit-testing would fire its own dragenter and desync the depth counter.
  host: {
    'aria-hidden': 'true',
    class:
      'position-absolute d-flex align-items-center justify-content-center gap-2 rounded-3 text-primary fs-6 fw-semibold pe-none',
  },
  templateUrl: './drop-overlay.html',
  styleUrl: './drop-overlay.scss',
})
export class DropOverlay {
  readonly label = input('Drop file(s)');
}
