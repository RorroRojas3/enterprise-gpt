import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MOBILE_VIEWPORT } from '@shared/utils/layout/breakpoints';
import { injectMediaQuery } from '@core/ui/media-query';
import { ShellChrome } from '@core/ui/shell-chrome-service';
import { Icon } from '@shared/components/icon/icon';
import { PillSubnav } from '@shared/components/pill-subnav/pill-subnav';
import { ADMIN_PILLS, ADMIN_TABS } from './admin-tabs';

/**
 * The admin rail beside the open tab. It holds no store, so opening a tab instantiates only that
 * tab's store.
 */
@Component({
  selector: 'app-admin-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, PillSubnav, RouterLink, RouterLinkActive, RouterOutlet],
  // The shell's `<main>` clips its overflow, so the area scrolls itself; `flex-fill` keeps the
  // rail spanning the pane when the tab is short.
  host: {
    class: 'd-flex flex-column flex-md-row gap-3 gap-md-4 p-3 p-md-4 flex-fill overflow-y-auto',
  },
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
