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
import { ConversationDto } from '@core/api/conversation.model';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@core/errors/error-panel';
import { CHAT_ROUTE } from '@core/navigation/route-paths';
import { ProjectConversationsStore } from '@core/projects/project-conversations-store';
import { ProjectPicker } from '@core/projects/project-picker';
import { DataTable } from '@shared/data-table/data-table';
import { TableCell } from '@shared/data-table/table-cell';
import { TableColumn } from '@shared/data-table/table-column.model';
import { EmptyState } from '@shared/empty-state/empty-state';
import { formatRelativeTime } from '@shared/formatting/relative-time';
import { Icon } from '@shared/icons/icon';
import { Menu } from '@shared/menu/menu';
import { MenuItem } from '@shared/menu/menu-item';
import { MenuSeparator } from '@shared/menu/menu-separator';

/**
 * The store is provided on the detail route, not here, so the tab strip's count survives a switch
 * to another panel. Row actions go through `ConversationActionsStore`, as everywhere else.
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
  host: { class: 'd-block' },
  templateUrl: './project-conversations.html',
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

  /** A field, not a template literal: exhaustive `checkNoChanges` sees a template array as new. */
  protected readonly columns: readonly TableColumn<ConversationDto>[] = [
    { key: 'name', header: 'Name', width: 'minmax(0, 1fr)', mobile: 'title' },
    { key: 'updated', header: 'Updated', width: '110px', mobile: 'meta' },
    {
      key: 'favorite',
      header: 'Favourite',
      headerHidden: true,
      width: '32px',
      align: 'center',
      mobile: 'trailing',
    },
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '36px',
      align: 'center',
      mobile: 'trailing',
    },
  ];

  protected readonly trackKey = (conversation: ConversationDto): string => conversation.id;
  protected readonly rowLabel = (conversation: ConversationDto): string => conversation.name;

  /**
   * Computed into a map once per result set: a clock read in a binding changes between exhaustive
   * check passes, and per-row date formatting is real work on a paging list.
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
   * One picker, outside the table so a row re-render cannot destroy it. The row id is captured, not
   * the DTO: `toUpdateBody` echoes the name, and a stale snapshot would revert a concurrent rename.
   */
  protected readonly pickerOpen = signal(false);
  private readonly _pickerRowId = signal<string | null>(null);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  /** The reader pressed Load more and focus has to survive the control removing itself. */
  private readonly _focusAfterPage = signal(false);

  /** The row whose own action will evict it, so focus can be moved when it goes. */
  private readonly _focusAfterEviction = signal<string | null>(null);

  constructor() {
    // Load more disappears when the last page lands, taking focus with it.
    effect(() => {
      const settled = !this.conversations.loadingMore();
      const requested = untracked(this._focusAfterPage);

      if (!settled || !requested) {
        return;
      }

      this._focusAfterPage.set(false);

      // Still there: the reader is paging through, so focus stays on the control.
      if (untracked(this.conversations.hasMore)) {
        return;
      }

      afterNextRender(() => this._recoverFocus(), { injector: this._injector });
    });

    // Removal and a completed move evict their row optimistically, destroying the kebab that
    // `Menu.close()` and `ProjectPicker.close()` just returned focus to.
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
        // Still there and settled: the request failed or was refused. Left armed, this would fire
        // on an unrelated later removal.
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
    // Armed at the open: any project the reader picks evicts the row, and the effect disarms
    // itself when a dismissed picker leaves it in place.
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
   * Arms focus recovery at the gesture rather than inferring it when the list shrinks; the id lets
   * the effect tell a confirmed eviction from a failed or refused request.
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
   * Only when focus actually fell through. The first row, as rows shift under an eviction; else
   * `#main-content`, since this panel has no control outside every branch of its template.
   */
  private _recoverFocus(): void {
    const active = this._document.activeElement;
    if (active !== null && active !== this._document.body && active.isConnected) {
      return;
    }

    const first = this._host.nativeElement.querySelector<HTMLElement>('[data-row-anchor]');

    (first ?? this._document.getElementById('main-content'))?.focus();
  }
}
