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
import { ConversationDto } from '@domain/api/conversation';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { canRetry } from '@core/errors/error-message';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { SearchInput } from '@shared/form/search-input/search-input';
import { BulkActionBar } from '@shared/data/bulk-action-bar/bulk-action-bar';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { Icon } from '@shared/icon/icon';
import { Tooltip } from '@shared/overlay/tooltip/tooltip';
import { ConversationLibraryStore } from './conversation-library-store';
import { DeleteConversationsDialog } from './delete-conversations-dialog';
import {
  FAVORITES_ON,
  FAVORITES_PARAM,
  NAME_PARAM,
  ORDER_EXPLANATION,
  shouldReplaceHistory,
} from './conversations-route';

/**
 * The conversations library, frame `4a` (US-701).
 *
 * **The URL is the source of truth for the query.** The field is a view of it and the
 * store caches the rows it produced, so there is exactly one writer — the `searched`
 * handler — and one reader path: `withComponentInputBinding()` binds `?name=` to
 * {@link name}, which drives the store. That is also what makes the back button
 * restore the previous result set without a line of code, and what makes a filtered
 * list shareable as a link.
 *
 * The loop cannot feed itself. A navigation changes {@link name}, which re-seeds
 * `SearchInput`'s `linkedSignal`; its debounce then compares the new text against the
 * value it already holds, finds them equal, and emits nothing. Two rules keep that
 * true: bind `[value]` one-way, never `[(value)]`, and never navigate from an effect
 * over `name()` — only from the user's own gesture.
 *
 * Frame `4a` also draws a project chip and a per-row kebab, which arrive with US-307.
 * What the frame deliberately does *not* draw is a sort control, and that stays absent
 * until US-706 gives the server one to honour — US-705's whole subject.
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
    RouterLink,
    SearchInput,
    TableCell,
    Tooltip,
  ],
  providers: [ConversationLibraryStore],
  templateUrl: './conversations.html',
  styleUrl: './conversations.scss',
})
export class Conversations {
  /**
   * Bound from `?name=` by `withComponentInputBinding()`.
   *
   * Trimmed in the transform because the router writes `undefined` when the key
   * leaves the URL, and a hand-edited `?name=%20%20` has to mean unfiltered.
   */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  /**
   * Bound from `?favorites=` by `withComponentInputBinding()` (US-703).
   *
   * Anything other than the one value the screen writes reads as off, so a
   * hand-edited `?favorites=yes` cannot put the toolbar and the request out of step.
   */
  readonly favorites = input(false, {
    transform: (value: string | undefined) => value === FAVORITES_ON,
  });

  protected readonly library = inject(ConversationLibraryStore);
  protected readonly actions = inject(ConversationActionsStore);

  private readonly _models = inject(ModelCatalogStore);
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

  /** The bulk-delete confirmation (US-704). View state, so it lives here, not in the store. */
  protected readonly confirmOpen = signal(false);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly ORDER_EXPLANATION = ORDER_EXPLANATION;

  protected readonly chatRoute = CHAT_ROUTE;

  /**
   * Frame `4b`'s heading, naming the term.
   *
   * Read from the **store**, not from {@link name}: the store's copy is the one the
   * rendered rows belong to, so the heading cannot name a term whose results have not
   * arrived yet.
   */
  protected readonly noMatchHeading = computed(
    () => `No conversations match “${this.library.name()}”`,
  );

  /**
   * Fields rather than template literals. `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares bindings between passes, and an array or an arrow allocated in
   * the template is a new value every check.
   */
  protected readonly columns: readonly TableColumn<ConversationDto>[] = [
    { key: 'name', header: 'Name', width: 'minmax(0, 1fr)', mobile: 'title' },
    { key: 'model', header: 'Model', width: '160px', mobile: 'meta' },
    // headerHidden rather than an empty header: the column still needs a name for a
    // screen reader listing the table's columns, and the board draws no label here.
    {
      key: 'favorite',
      header: 'Favourite',
      headerHidden: true,
      width: '36px',
      align: 'center',
      mobile: 'actions',
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

  constructor() {
    // The constructor is an injection context, which a signal-valued reactive method
    // requires: it binds the subscription to this screen rather than to the injector
    // that outlives it. Handing over a computation — not the values — is what makes a
    // deep link issue one request carrying both parameters, rather than an unfiltered
    // request followed by a filtered one.
    this.library.bindQuery(() => ({ name: this.name(), favorites: this.favorites() }));

    // Focus, not state — the one thing a signal cannot express declaratively. Load
    // more removes itself when the last page lands, so the reader who pressed it is
    // left standing on a node that no longer exists and focus falls to `<body>`; the
    // Clear-search button has the same shape and is handled the same way.
    effect(() => {
      const settled = !this.library.loadingMore();
      // Untracked, so clearing the request below cannot re-enter this effect, and so
      // the only thing it waits on is the page.
      const requested = untracked(this._focusAfterPage);

      if (!settled || !requested) {
        return;
      }

      this._focusAfterPage.set(false);

      // Still there and still focused: the reader is paging through and moving focus
      // off the control they are pressing would be the worse defect.
      if (untracked(this.library.hasMore)) {
        return;
      }

      afterNextRender(() => this._focusLastRow(), { injector: this._injector });
    });

    // The same shape, for the other control on this screen that removes itself: a
    // star pressed under the favourites filter evicts its own row when the server
    // confirms it (US-703).
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
    // Only the direction that removes the row. Starring one under the filter leaves
    // it exactly where it is.
    this._focusAfterEviction.set(
      this.favorites() && conversation.isFavorite ? conversation.id : null,
    );
    this.actions.toggleFavorite(conversation);
  }

  protected onLoadMore(): void {
    this.library.loadMore();
    // Only when a page actually started. The control carries `aria-disabled` rather
    // than `disabled`, so a press during a narrowing search still reaches this — and
    // `loadMore()` no-ops there, leaving `loadingMore` false and a flag that would
    // never be cleared.
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

  protected toggleFavorites(): void {
    const next = !this.favorites();

    // Never replaces. A discrete on/off is the add-or-remove transition
    // `shouldReplaceHistory` exists to push at — one of its two arguments would always
    // be empty here — so back undoes turning the filter on, and turning it off.
    this._applyQuery({ [FAVORITES_PARAM]: next ? FAVORITES_ON : null }, false);
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a list the reader then has to find again. The field is outside
    // every branch of the template, so it is always connected.
    this._search().focusField();
  }

  /** The favourites empty state's way out, which is not the same as clearing a search. */
  protected clearFavorites(): void {
    this._applyQuery({ [FAVORITES_PARAM]: null }, false);
    // Same reason as `clearSearch`: this button lives inside the empty state and is
    // removed the moment rows come back, so focus would fall to `<body>`.
    this._search().focusField();
  }

  protected favoriteLabel(conversation: ConversationDto): string {
    return `${conversation.isFavorite ? 'Unfavourite' : 'Favourite'} ${conversation.name}`;
  }

  /**
   * Puts focus on the last row after the final page, since the control that was
   * holding it has gone.
   *
   * The last row rather than the first new one: it is where the vanished button was,
   * so the reader's place on the page is preserved rather than jumped. The field is
   * the fallback because it is the only control outside every branch of the template.
   */
  private _focusLastRow(): void {
    // Only when focus actually fell through. A reader who clicked into the search
    // field while the page was on the wire must not be yanked out of it — the same
    // test the delete dialog applies before its own fallback.
    const active = this._document.activeElement;
    const fellThrough = active === null || active === this._document.body || !active.isConnected;

    if (!fellThrough) {
      return;
    }

    const rows = this._host.nativeElement.querySelectorAll<HTMLElement>('.conversations__name');
    const last = rows.item(rows.length - 1);

    if (last === null) {
      this._search().focusField();
      return;
    }

    last.focus();
  }

  /**
   * Moves focus to the search field after a row removed itself.
   *
   * The field rather than a neighbouring row: the rows shift under an eviction, so
   * "the next one" is not a place the reader chose, and the field is the one control
   * outside every branch of the template.
   */
  private _focusField(): void {
    const active = this._document.activeElement;

    if (active === null || active === this._document.body || !active.isConnected) {
      this._search().focusField();
    }
  }

  /**
   * The single writer. Every query change is a navigation and nothing writes the
   * store directly, which is what keeps the URL authoritative and the back button
   * working without a line of code.
   */
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
