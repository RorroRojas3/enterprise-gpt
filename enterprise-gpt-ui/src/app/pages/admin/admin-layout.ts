import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ShellChrome } from '@core/ui/shell-chrome';
import { Icon } from '@shared/icon/icon';
import { PillSubnav } from '@shared/nav/pill-subnav/pill-subnav';
import { MOBILE_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';
import { ADMIN_PILLS, ADMIN_TABS } from './admin-tabs';

/**
 * The admin rail beside the open tab. It holds no store, so opening a tab instantiates only that
 * tab's store.
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

  /** Fixed at `DataTable`'s breakpoint, so the rail and the rows restructure at one width. */
  protected readonly isNarrow = injectMediaQuery(MOBILE_VIEWPORT);

  constructor() {
    // Optional because specs render this without a shell. Cleared on destroy, or the title
    // would outlive the area and caption the chat screen.
    const chrome = inject(ShellChrome, { optional: true });
    if (chrome !== null) {
      chrome.publish({ title: 'Admin' });
      inject(DestroyRef).onDestroy(() => chrome.clear());
    }
  }
}
