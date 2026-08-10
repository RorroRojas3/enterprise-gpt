import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

export interface PillItem {
  readonly id: string;
  readonly label: string;
  readonly link: readonly unknown[] | string;
}

/**
 * The horizontal pill navigation the admin area uses below 768px — frame `5m`.
 *
 * Pills are 44px tall rather than the board's ~31px. The board's own caption asks for
 * 44px targets at this breakpoint, so this is the design's intent rather than a
 * departure from it.
 */
@Component({
  selector: 'app-pill-subnav',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './pill-subnav.html',
  styleUrl: './pill-subnav.scss',
})
export class PillSubnav {
  readonly items = input.required<readonly PillItem[]>();

  /** Names this nav apart from every other one on the page. */
  readonly ariaLabel = input.required<string>();
}
