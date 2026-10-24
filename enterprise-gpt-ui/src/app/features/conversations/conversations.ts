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
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Params, Router, RouterLink } from '@angular/router';
import { ConversationDto } from '@core/api/conversation.model';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { canRetry } from '@core/errors/error-message';
import { ErrorPanel } from '@core/errors/error-panel';
import { CHAT_ROUTE } from '@core/navigation/route-paths';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { ProjectPicker } from '@core/projects/project-picker';
import { BulkActionBar } from '@shared/data-table/bulk-action-bar';
import { DataTable } from '@shared/data-table/data-table';
import { TableCell } from '@shared/data-table/table-cell';
import { TableColumn } from '@shared/data-table/table-column.model';
import { EmptyState } from '@shared/empty-state/empty-state';
import { Icon } from '@shared/icons/icon';
import { Menu } from '@shared/menu/menu';
import { MenuItem } from '@shared/menu/menu-item';
import { MenuSeparator } from '@shared/menu/menu-separator';
import { shouldReplaceHistory } from '@shared/navigation/search-history';
import { SearchInput } from '@shared/search/search-input';
import { ConversationLibraryStore } from './conversation-library-store';
import {
  CONVERSATION_SORTS,
  DEFAULT_CONVERSATION_SORT,
  FAVORITES_ON,
  FAVORITES_PARAM,
  NAME_PARAM,
  SORT_PARAM,
  sortKey,
  toConversationSort,
} from './conversations-route';
import { DeleteConversationsDialog } from './delete-conversations-dialog';

/**
 * The URL is the query's source of truth. Bind `SearchInput`'s `[value]` one-way and never
 * navigate from an effect over `name()`, or the URL-to-field loop feeds itself.
 */
@Component({
  selector: 'app-conversations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BulkActionBar,
    DataTable,
    DeleteConversationsDialog,
    EmptyState,
    ErrorPanel,
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    ProjectPicker,
    RouterLink,
    SearchInput,
    TableCell,
  ],
  providers: [ConversationLibraryStore],
  // The shell's `<main>` clips its overflow, so a routed screen scrolls itself.
  host: { class: 'd-flex flex-column gap-3 p-3 p-md-4 overflow-y-auto' },
  templateUrl: './conversations.html',
})
export class Conversations {
  /** Bound from `?name=`. Trimmed, so a hand-edited `?name=%20%20` means unfiltered. */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  /** Bound from `?favorites=`; anything but the value the screen writes reads as off. */
  readonly favorites = input(false, {
    transform: (value: string | undefined) => value === FAVORITES_ON,
  });

  /** Bound from `?sort=`, resolved to an offered option so the select and the request agree. */
  readonly sort = input(DEFAULT_CONVERSATION_SORT, { transform: toConversationSort });

  protected readonly library = inject(ConversationLibraryStore);
  protected readonly actions = inject(ConversationActionsStore);

  private readonly _models = inject(ModelCatalogStore);
  private readonly _projects = inject(ProjectLookupStore);
  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  private readonly _injector = inject(Injector);
  private readonly _document = inject(DOCUMENT);
  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly _search = viewChild.required(SearchInput);

  /** The reader pressed Load more and focus has to survive the page landing. */
  private readonly _focusAfterPage = signal(false);

  /** The row whose star will evict it, so focus can be moved when it goes. */
  private readonly _focusAfterEviction = signal<string | null>(null);

  /** The bulk-delete confirmation; view state, so it lives here rather than in the store. */
  protected readonly confirmOpen = signal(false);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly sortOptions = CONVERSATION_SORTS;

  protected readonly chatRoute = CHAT_ROUTE;

  /** The store's term, not {@link name}: the one the rendered rows belong to. */
  protected readonly noMatchHeading = computed(
    () => `No conversations match “${this.library.name()}”`,
  );

  /** A field, not a template literal: exhaustive checkNoChanges would see a new array each pass. */
  protected readonly columns: readonly TableColumn<ConversationDto>[] = [
    { key: 'name', header: 'Name', width: 'minmax(0, 1fr)', mobile: 'title' },
    // `badges`, not `meta`, on mobile: the chip is a labelled token, which that slot is styled for.
    { key: 'project', header: 'Project', width: '180px', mobile: 'badges' },
    { key: 'model', header: 'Model', width: '160px', mobile: 'meta' },
    // headerHidden rather than an empty header, so screen readers can still name the column.
    {
      key: 'favorite',
      header: 'Favourite',
      headerHidden: true,
      width: '36px',
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

  /** Built once per catalogue change rather than scanned per row, per check. */
  private readonly _modelNames = computed(
    () => new Map(this._models.models().map((model) => [model.id, model.name])),
  );

  /** The model's name, or an em dash: `ConversationDto` carries only its id. */
  protected readonly modelName = (conversation: ConversationDto): string =>
    (conversation.modelId === null ? undefined : this._modelNames().get(conversation.modelId)) ??
    '—';

  /**
   * The project's name, or null so the chip is absent rather than empty. Also null past the
   * lookup store's drain ceiling, where a dash would wrongly say "none".
   */
  protected readonly projectName = (conversation: ConversationDto): string | null =>
    this._projects.nameOf()(conversation.projectId);

  /**
   * One picker for the table, not one per row: a row re-render would destroy it. It holds the
   * row's id, not its DTO, so a rename landing while it is open is not reverted by a stale name.
   */
  protected readonly pickerOpen = signal(false);
  private readonly _pickerRowId = signal<string | null>(null);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  protected readonly pickerRow = computed(() => {
    const id = this._pickerRowId();

    return id === null ? null : (this.library.entityMap()[id] ?? null);
  });

  /** Opens the picker for one row, anchored to the kebab that opened it. */
  protected openPicker(conversation: ConversationDto, trigger: Menu): void {
    if (this.actions.isRowPending(conversation.id)) {
      return;
    }

    this.pickerAnchor.set(trigger.triggerElement());
    this._pickerRowId.set(conversation.id);
    this.pickerOpen.set(true);
  }

  constructor() {
    this.library.bindQuery(() => ({
      name: this.name(),
      favorites: this.favorites(),
      sort: this.sort(),
    }));

    // Load more removes itself when the last page lands, so focus would fall to `<body>`.
    effect(() => {
      const settled = !this.library.loadingMore();
      // Untracked, so clearing it below cannot re-run this effect; only the page drives it.
      const requested = untracked(this._focusAfterPage);

      if (!settled || !requested) {
        return;
      }

      this._focusAfterPage.set(false);

      // Load more is still there and focused, so the reader keeps paging from it.
      if (untracked(this.library.hasMore)) {
        return;
      }

      afterNextRender(() => this._focusLastRow(), { injector: this._injector });
    });

    // A star pressed under the favourites filter evicts its own row once the server confirms.
    effect(() => {
      // Both tracked: the first is the write that removes the row, the second is how
      // this knows a request that removed nothing is over.
      const loaded = this.library.entityMap();
      const pending = this.actions.pendingIds();
      const id = untracked(this._focusAfterEviction);

      if (id === null) {
        return;
      }

      if (loaded[id] !== undefined) {
        // Still there and settled — the request failed, or the guard refused it.
        // Leaving the id set would fire this against some unrelated later removal.
        if (!pending.has(id)) {
          this._focusAfterEviction.set(null);
        }
        return;
      }

      this._focusAfterEviction.set(null);
      afterNextRender(() => this._focusField(), { injector: this._injector });
    });
  }

  protected onToggleFavorite(conversation: ConversationDto): void {
    // Only unstarring under the filter removes the row.
    this._focusAfterEviction.set(
      this.favorites() && conversation.isFavorite ? conversation.id : null,
    );
    this.actions.toggleFavorite(conversation);
  }

  protected onLoadMore(): void {
    this.library.loadMore();
    // Only if a page started: an aria-disabled press still lands here, and a flag set while
    // `loadMore()` no-ops would never be cleared.
    this._focusAfterPage.set(this.library.loadingMore());
  }

  protected onSearched(term: string): void {
    const next = term.trim();

    this._applyQuery(
      // `null` removes the key rather than leaving `?name=` behind.
      { [NAME_PARAM]: next === '' ? null : next },
      shouldReplaceHistory(this.name(), next),
    );
  }

  /** Records the chosen order in the URL; a value outside the offered set means a tampered DOM. */
  protected onSorted(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const next = this.sortOptions.find((option) => option.value === value);

    if (next === undefined || next.value === this.sort().value) {
      return;
    }

    const previousKey = sortKey(this.sort());
    const nextKey = sortKey(next);

    this._applyQuery(
      { [SORT_PARAM]: nextKey === '' ? null : nextKey },
      // A closed `<select>` fires `change` per arrow key, so only turning an order on or off
      // pushes; moving between two orders replaces.
      shouldReplaceHistory(previousKey, nextKey),
    );
  }

  protected toggleFavorites(): void {
    const next = !this.favorites();

    // Always pushes: a discrete on/off is exactly the transition `shouldReplaceHistory` pushes for.
    this._applyQuery({ [FAVORITES_PARAM]: next ? FAVORITES_ON : null }, false);
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself once rows come back; the field sits outside every template
    // branch, so it is always there to take focus.
    this._search().focusField();
  }

  /** The favourites empty state's way out, which is not the same as clearing a search. */
  protected clearFavorites(): void {
    this._applyQuery({ [FAVORITES_PARAM]: null }, false);
    // As in `clearSearch`: this button goes once rows come back.
    this._search().focusField();
  }

  protected favoriteLabel(conversation: ConversationDto): string {
    return `${conversation.isFavorite ? 'Unfavourite' : 'Favourite'} ${conversation.name}`;
  }

  /**
   * After the final page, focuses the last row, where the vanished button was; the field is the
   * fallback, being the only control outside every template branch.
   */
  private _focusLastRow(): void {
    // Only if focus fell through: a reader who clicked into the field meanwhile stays there.
    const active = this._document.activeElement;
    const fellThrough = active === null || active === this._document.body || !active.isConnected;

    if (!fellThrough) {
      return;
    }

    const rows = this._host.nativeElement.querySelectorAll<HTMLElement>('[data-row-anchor]');
    const last = rows.item(rows.length - 1);

    if (last === null) {
      this._search().focusField();
      return;
    }

    last.focus();
  }

  /**
   * Focuses the field after a row evicts itself: the rows shift, so a neighbour is not a place
   * the reader chose.
   */
  private _focusField(): void {
    const active = this._document.activeElement;

    if (active === null || active === this._document.body || !active.isConnected) {
      this._search().focusField();
    }
  }

  /** The single writer: every query change is a navigation, so the URL stays authoritative. */
  private _applyQuery(queryParams: Params, replaceUrl: boolean): void {
    void this._router.navigate([], {
      relativeTo: this._route,
      queryParams,
      // A search has to survive the favourites filter, and vice versa.
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
