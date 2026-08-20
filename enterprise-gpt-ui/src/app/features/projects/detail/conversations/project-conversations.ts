import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { ConversationDto } from '@domain/api/conversation';
import { formatRelativeTime } from '@domain/format/relative-time';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { canRetry } from '@core/errors/error-message';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { ProjectPicker } from '@shared/projects/project-picker/project-picker';
import { ProjectConversationsStore } from '@core/projects/project-conversations-store';

/**
 * A project's conversations (US-908, frame `4g`).
 *
 * Four columns — name, updated, star, kebab — and deliberately none of the library's
 * toolbar: frame `4g` draws no search field, no favourites filter and no selection, so
 * none of them ship. The row actions are the five US-307 built, routed through
 * `ConversationActionsStore` exactly as the sidebar row and the library table are;
 * nothing here reimplements a flow.
 *
 * The store is **not** provided here. It is on the detail route, so the tab strip's
 * count survives a switch to another panel — the same placement, for the same reason, as
 * `ProjectDocumentsStore`.
 */
@Component({
  selector: 'app-project-conversations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DataTable,
    EmptyState,
    ErrorPanel,
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    ProjectPicker,
    RouterLink,
    TableCell,
  ],
  templateUrl: './project-conversations.html',
  styleUrl: './project-conversations.scss',
})
export class ProjectConversations {
  protected readonly conversations = inject(ProjectConversationsStore);
  protected readonly actions = inject(ConversationActionsStore);

  private readonly _document = inject(DOCUMENT);
  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly _injector = inject(Injector);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly chatRoute = CHAT_ROUTE;

  /**
   * Frame `4g`'s tracks: `1fr 110px 32px 36px`. Fields rather than template literals,
   * because `provideCheckNoChangesConfig({ exhaustive: true })` compares bindings
   * between passes and an array allocated in the template is new on every check.
   */
  protected readonly columns: readonly TableColumn<ConversationDto>[] = [
    { key: 'name', header: 'Name', width: 'minmax(0, 1fr)', mobile: 'title' },
    { key: 'updated', header: 'Updated', width: '110px', mobile: 'meta' },
    {
      key: 'favorite',
      header: 'Favourite',
      headerHidden: true,
      width: '32px',
      align: 'center',
      mobile: 'actions',
    },
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '36px',
      align: 'center',
      mobile: 'actions',
    },
  ];

  protected readonly trackKey = (conversation: ConversationDto): string => conversation.id;
  protected readonly rowLabel = (conversation: ConversationDto): string => conversation.name;

  /**
   * Computed once per result set into a map, exactly as the projects grid does.
   *
   * Two separate reasons, and both bite. A clock read from a template binding is a fresh
   * value on every change-detection pass, which `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares between passes; and `formatRelativeTime` does calendar arithmetic
   * and, in its date band, a `toLocaleDateString` — running that per visible row per pass,
   * doubled by the exhaustive pass, is real work on a list that pages. A lookup is not.
   * The labels are minute-granular at their finest, so nothing visible is lost.
   */
  private readonly _updatedLabels = computed(() => {
    const now = Date.now();

    return new Map(
      this.conversations
        .entities()
        .map((conversation) => [
          conversation.id,
          formatRelativeTime(conversation.dateModified, now),
        ]),
    );
  });

  protected readonly updatedLabel = (conversation: ConversationDto): string =>
    this._updatedLabels().get(conversation.id) ?? '';

  protected readonly favoriteLabel = (conversation: ConversationDto): string =>
    `${conversation.isFavorite ? 'Unfavourite' : 'Favourite'} ${conversation.name}`;

  /**
   * One picker for the panel, mounted outside the table so a row re-render cannot
   * destroy the panel the reader is using.
   *
   * The **id** is captured and the DTO derived, never the other way round:
   * `toUpdateBody` echoes the target's name, and a frozen snapshot would send back a
   * stale one, reverting a rename that landed while the panel was open.
   */
  protected readonly pickerOpen = signal(false);
  private readonly _pickerRowId = signal<string | null>(null);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  /** The reader pressed Load more and focus has to survive the control removing itself. */
  private readonly _focusAfterPage = signal(false);

  /** The row whose own action will evict it, so focus can be moved when it goes. */
  private readonly _focusAfterEviction = signal<string | null>(null);

  constructor() {
    // Load more disappears when the last page lands, leaving the reader standing on a
    // node that no longer exists. The same shape the conversations library uses.
    effect(() => {
      const settled = !this.conversations.loadingMore();
      const requested = untracked(this._focusAfterPage);

      if (!settled || !requested) {
        return;
      }

      this._focusAfterPage.set(false);

      // Still there and still focused: the reader is paging through, and moving focus
      // off the control they are pressing would be the worse defect.
      if (untracked(this.conversations.hasMore)) {
        return;
      }

      afterNextRender(() => this._recoverFocus(), { injector: this._injector });
    });

    // Remove-from-project and a completed move both evict their own row, destroying the
    // kebab that `Menu.close()` and `ProjectPicker.close()` have already returned focus
    // to — optimistically, before the server confirmed.
    effect(() => {
      // Both tracked: the first is the write that removes the row, the second is how
      // this knows a request that removed nothing has settled.
      const loaded = this.conversations.entityMap();
      const pending = this.actions.pendingIds();
      const id = untracked(this._focusAfterEviction);

      if (id === null) {
        return;
      }

      if (loaded[id] !== undefined) {
        // Still there and settled — the request failed, or a guard refused it. Leaving
        // the id armed would fire this against some unrelated later removal.
        if (!pending.has(id)) {
          this._focusAfterEviction.set(null);
        }

        return;
      }

      this._focusAfterEviction.set(null);
      afterNextRender(() => this._recoverFocus(), { injector: this._injector });
    });
  }

  protected readonly pickerRow = computed(() => {
    const id = this._pickerRowId();

    return id === null ? null : (this.conversations.entityMap()[id] ?? null);
  });

  protected openPicker(conversation: ConversationDto, trigger: Menu): void {
    if (this.actions.isRowPending(conversation.id)) {
      return;
    }

    this.pickerAnchor.set(trigger.triggerElement());
    this._pickerRowId.set(conversation.id);
    this.pickerOpen.set(true);
    // Armed at the open, not at the choice: every row here is in this project, so any
    // project the reader picks evicts it. A dismissed picker leaves the row in place,
    // and the effect disarms itself on exactly that.
    this._focusAfterEviction.set(conversation.id);
  }

  /** Releases the detached anchor and the row the panel was acting on. */
  protected onPickerClosed(open: boolean): void {
    this.pickerOpen.set(open);
    if (!open) {
      this._pickerRowId.set(null);
      this.pickerAnchor.set(null);
    }
  }

  /**
   * Arms the focus fixup for the two actions on this panel that destroy the control
   * being pressed: Remove from project, and a move to another project.
   *
   * Armed at the gesture rather than inferred from the list shrinking — by the time the
   * row goes, nothing on screen says the reader caused it — and the id is what lets the
   * effect below tell a confirmed eviction from a request that failed or was refused.
   */
  protected removeFromProject(conversation: ConversationDto): void {
    this._focusAfterEviction.set(conversation.id);
    this.actions.moveToProject(conversation, null);
  }

  protected onLoadMore(): void {
    this._focusAfterPage.set(true);
    this.conversations.loadMore();
  }

  /**
   * Moves focus to the first surviving row, or to the shell's `#main-content` landmark.
   *
   * The first row rather than a neighbour of the one that left: the rows shift under an
   * eviction, so "the next one" is not a place the reader chose. `#main-content` is the
   * fallback because this panel — unlike the conversations library, which has a search
   * field — has no control outside every branch of its own template.
   *
   * Only ever when focus **actually fell through**: a reader who clicked elsewhere while
   * the request was on the wire must not be yanked back.
   */
  private _recoverFocus(): void {
    const active = this._document.activeElement;
    if (active !== null && active !== this._document.body && active.isConnected) {
      return;
    }

    const first = this._host.nativeElement.querySelector<HTMLElement>(
      '.project-conversations__name',
    );

    (first ?? this._document.getElementById('main-content'))?.focus();
  }
}
