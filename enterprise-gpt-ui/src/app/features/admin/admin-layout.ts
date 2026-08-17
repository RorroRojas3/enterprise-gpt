import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Icon } from '@shared/icon/icon';
import { PillSubnav } from '@shared/nav/pill-subnav/pill-subnav';
import { injectMediaQuery } from '@shared/layout/media-query';
import { ADMIN_PILLS, ADMIN_TABS } from './admin-tabs';

/**
 * The administration area's chrome — `AdminNav.dc.html`'s 190px rail beside the tab
 * that is open.
 *
 * A layout component rather than chrome repeated per tab: the rail marks the active
 * entry from the router, which is what makes the tabs routes rather than local state
 * (US-1209), and a second copy would be a second thing to keep in step.
 *
 * It holds **no store**. US-1209 requires that opening a tab instantiates that tab's
 * store and no other, so every store in this area is provided on the tab's own route.
 */
@Component({
  selector: 'app-admin-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, PillSubnav, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './admin-layout.html',
  styleUrl: './admin-layout.scss',
})
export class AdminLayout {
  protected readonly tabs = ADMIN_TABS;
  protected readonly pills = ADMIN_PILLS;

  /**
   * The rail becomes frame `5m`'s scrollable pill strip below 768px.
   *
   * The same breakpoint `DataTable` collapses at, and fixed rather than an input for
   * the same reason: the boards restructure at one width, and a caller choosing a
   * different one would put the rail and the rows out of step.
   */
  protected readonly isNarrow = injectMediaQuery('(max-width: 767px)');
}
