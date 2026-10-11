import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

export interface PillItem {
  readonly id: string;
  readonly label: string;
  readonly link: readonly unknown[] | string;

  /** Withheld while `null`, not shown as zero: "Files 0" on a project with four is worse. */
  readonly count?: number | null;
}

/** The admin area's pill navigation below 768px, with 44px touch targets. */
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
