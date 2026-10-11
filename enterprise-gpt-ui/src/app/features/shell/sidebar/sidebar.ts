import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import {
  ADMIN_ROUTE,
  CHAT_ROUTE,
  CONVERSATIONS_ROUTE,
  DOCUMENTS_ROUTE,
  PROJECTS_ROUTE,
} from '@core/auth/auth-routes';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { canRetry } from '@core/errors/error-message';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { SessionStore } from '@core/session/session-store';
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
import { SidebarFavorites } from './favorite-projects';

/**
 * `overlay` omits the collapse chevron: the mobile drawer has no collapsed state, and the shell's
 * close button already sits there.
 */
export type SidebarMode = 'expanded' | 'strip' | 'overlay';

interface NavItem {
  readonly label: string;
  readonly icon: IconName;
  readonly link: string;
  /** Renders the shield glyph beside a permission-gated entry. */
  readonly restricted: boolean;
}

const SKELETON_ROWS = 6;

/**
 * Admin is absent, not disabled, for non-administrators. The two widths are separate branches: a
 * 44px tooltip target is a different control from a labelled 36px row.
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
    SidebarFavorites,
    Skeleton,
    Tooltip,
    UserFooter,
  ],
  templateUrl: './sidebar.html',
  styleUrl: './sidebar.scss',
})
export class Sidebar {
  private readonly _session = inject(SessionStore);
  private readonly _injector = inject(Injector);
  private readonly _search = viewChild(SearchInput);

  /** Both chevrons carry it, one at a time; re-focused after a toggle destroys the pressed one. */
  private readonly _collapseControl = viewChild<ElementRef<HTMLButtonElement>>('collapseControl');

  /** The expanded branch's Favorite projects section; absent on the strip. */
  private readonly _favorites = viewChild(SidebarFavorites);

  /**
   * Controlled by the shell, which alone knows the viewport: at tablet width the chevron opens a
   * transient overlay without touching the persisted preference.
   */
  readonly mode = input.required<SidebarMode>();

  /** Either chevron. What it means is the shell's to decide. */
  readonly toggled = output<void>();

  /** A strip control (search or star) asking to expand, whatever that means at this width. */
  readonly searchRequested = output<void>();

  protected readonly conversations = inject(ConversationListStore);
  protected readonly projects = inject(ProjectLookupStore);
  protected readonly collapsed = computed(() => this.mode() === 'strip');

  /** In the overlay, the shell's close button sits where this would. */
  protected readonly showCollapse = computed(() => this.mode() !== 'overlay');
  protected readonly chatRoute = CHAT_ROUTE;
  protected readonly skeletonRows = Array.from({ length: SKELETON_ROWS });

  /** A 403 on the list will answer the same way however often it is asked. */
  protected readonly canRetry = canRetry;

  protected readonly navItems = computed<readonly NavItem[]>(() => [
    { label: 'Chat', icon: 'bi-chat-square-text', link: CHAT_ROUTE, restricted: false },
    {
      label: 'Conversations',
      icon: 'bi-clock-history',
      link: CONVERSATIONS_ROUTE,
      restricted: false,
    },
    { label: 'Projects', icon: 'bi-folder2', link: PROJECTS_ROUTE, restricted: false },
    { label: 'Documents', icon: 'bi-file-earmark-text', link: DOCUMENTS_ROUTE, restricted: false },
    ...(this._session.isAdministrator()
      ? ([{ label: 'Admin', icon: 'bi-sliders', link: ADMIN_ROUTE, restricted: true }] as const)
      : []),
  ]);

  /** First page only: a narrowing search keeps its rows rather than flickering to skeletons. */
  protected readonly isFirstLoad = computed(
    () => this.conversations.isPending() && this.conversations.entities().length === 0,
  );

  /**
   * The toggle destroys the pressed button, so focus follows to its replacement after the render.
   * At tablet width this chevron is how the overlay is dismissed.
   */
  protected toggle(): void {
    this.toggled.emit();
    afterNextRender(() => this._collapseControl()?.nativeElement.focus(), {
      injector: this._injector,
    });
  }

  /** Focuses the field once the expanded branch has rendered it. */
  protected expandAndSearch(): void {
    this.searchRequested.emit();
    afterNextRender(() => this._search()?.focusField(), { injector: this._injector });
  }

  /** For the shell after dismissing the tablet overlay, instead of querying a shared class. */
  focusCollapseControl(): void {
    this._collapseControl()?.nativeElement.focus();
  }

  /** Where focus goes when the favourites section removes itself: the nearest control above. */
  protected focusSearch(): void {
    this._search()?.focusField();
  }

  protected clearSearch(): void {
    this.conversations.search('');
  }

  /** The strip's star reveals the tree rather than navigating; the section focuses itself. */
  protected expandToFavorites(): void {
    this.searchRequested.emit();
    afterNextRender(() => this._favorites()?.reveal(), { injector: this._injector });
  }
}
