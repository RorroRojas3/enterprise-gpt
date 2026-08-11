import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { Icon } from '@shared/icon/icon';

/**
 * Frame `2a`'s dashed drop affordance, rendered while a file drag is over a zone.
 *
 * Separate from {@link FileDropTarget} because the directive owns state, not pixels:
 * the overlay is specified down to the token, and a component is reviewable against
 * the board where DOM assembled in a directive is not. US-801's composer and US-902's
 * project files panel render the same one.
 *
 * `aria-hidden`, and deliberately: a drag in progress is a pointer gesture with no
 * keyboard equivalent, so there is no assistive-technology user for whom this element
 * is ever on screen. The accessible path to the same outcome is the attach button.
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
