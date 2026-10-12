import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Icon } from '@shared/components/icon/icon';

/**
 * The dashed affordance shown while a file drag is over a zone; separate from
 * {@link FileDropTarget}, which owns state. `aria-hidden` because a drag has no keyboard
 * equivalent: the attach button is the accessible path.
 */
@Component({
  selector: 'app-drop-overlay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  host: { 'aria-hidden': 'true' },
  templateUrl: './drop-overlay.html',
  styleUrl: './drop-overlay.scss',
})
export class DropOverlay {
  readonly label = input('Drop file(s)');
}
