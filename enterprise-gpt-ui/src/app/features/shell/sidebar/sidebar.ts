import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  viewChild,
} from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ADMIN_ROUTE, CHAT_ROUTE } from '@core/auth/auth-routes';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { canRetry } from '@core/errors/error-message';
import { SessionStore } from '@core/session/session-store';
import { UiStore } from '@core/ui/ui-store';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { SearchInput } from '@shared/form/search-input/search-input';
import { Icon } from '@shared/icon/icon';
import { IconName } from '@shared/icon/icon-names';
import { UserFooter } from '@shared/nav/user-footer/user-footer';
import { Tooltip } from '@shared/overlay/tooltip/tooltip';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
import { ConversationRow } from './conversation-row';

/** One entry in the sidebar's primary navigation. */
interface NavItem {
  readonly label: string;
  readonly icon: IconName;
  readonly link: string;
  /** Renders the shield glyph frame `3a` puts beside a permission-gated entry. */
  readonly restricted: boolean;
}

/** How many skeleton rows stand in for the list while the first page loads. */
const SKELETON_ROWS = 6;

/**
 * The persistent navigation of frames `3a` (260px) and `3b` (the 60px icon strip).
 *
 * **Only entries whose route exists are rendered.** The board draws five — Chat,
 * Conversations, Projects, Documents, Admin — but EP-7, EP-9 and EP-10 build the
 * middle three, and a link to a route that redirects back to `/chat` is worse than no
 * link. Each arrives with its own epic, the way US-203 has the Admin entry absent
 * rather than shown-and-disabled for a non-administrator.
 *
 * The **Favorite projects** section between the nav and the list is US-910 and is
 * deliberately not built: its pins are device-local until the backend enabler (US-909)
 * lands, and a section that has to say so has to exist before it can.
 *
 * The two widths are separate template branches rather than one branch with things
 * hidden, which is how `Sidebar.dc.html` itself is written: a 44px square target with
 * a tooltip is a different control from a 36px row with a visible label, not the same
 * control with its text removed.
 */
@Component({
  selector: 'app-sidebar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BrandLogo,
    ConversationRow,
    EmptyState,
    ErrorPanel,
    Icon,
    Ridgeline,
    RouterLink,
    RouterLinkActive,
    SearchInput,
    Skeleton,
    Tooltip,
    UserFooter,
  ],
  templateUrl: './sidebar.html',
  styleUrl: './sidebar.scss',
})
export class Sidebar {
  private readonly _session = inject(SessionStore);
  private readonly _ui = inject(UiStore);
  private readonly _injector = inject(Injector);
  private readonly _search = viewChild(SearchInput);

  protected readonly conversations = inject(ConversationListStore);
  protected readonly collapsed = this._ui.sidebarCollapsed;
  protected readonly chatRoute = CHAT_ROUTE;
  protected readonly skeletonRows = Array.from({ length: SKELETON_ROWS });

  /** A 403 on the list will answer the same way however often it is asked. */
  protected readonly canRetry = canRetry;

  protected readonly navItems = computed<readonly NavItem[]>(() => [
    { label: 'Chat', icon: 'bi-chat-square-text', link: CHAT_ROUTE, restricted: false },
    ...(this._session.isAdministrator()
      ? ([{ label: 'Admin', icon: 'bi-sliders', link: ADMIN_ROUTE, restricted: true }] as const)
      : []),
  ]);

  /**
   * The skeleton stands in only for the *first* page. A search that narrows an
   * already-populated list keeps its rows on screen with the field marked busy,
   * because replacing them with grey bars on every keystroke is the flicker this
   * distinction exists to avoid.
   */
  protected readonly isFirstLoad = computed(
    () => this.conversations.isPending() && this.conversations.entities().length === 0,
  );

  protected toggle(): void {
    this._ui.toggleSidebar();
  }

  /**
   * The strip's search button. Expanding alone would leave the user to find and click
   * the field they just asked for, so focus follows it — after the next render,
   * because the field does not exist in the DOM until the expanded branch is drawn.
   */
  protected expandAndSearch(): void {
    this._ui.setSidebarCollapsed(false);
    afterNextRender(() => this._search()?.focusField(), { injector: this._injector });
  }

  protected clearSearch(): void {
    this.conversations.search('');
  }
}
