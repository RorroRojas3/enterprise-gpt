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
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { canRetry } from '@core/errors/error-message';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { SearchInput } from '@shared/form/search-input/search-input';
import { Icon } from '@shared/icon/icon';
import { DocumentGroup, DocumentLibraryStore } from './document-library-store';
import { DocumentRow } from './document-row';
import {
  NAME_PARAM,
  SOURCE_GENERATED,
  SOURCE_PARAM,
  VIEW_FLAT,
  VIEW_PARAM,
  shouldReplaceHistory,
} from './documents-route';

/** How many skeleton rows stand in for the list while the first page loads. */
const SKELETON_ROWS = 6;

/**
 * The documents library: every document the signed-in user holds in a conversation —
 * uploaded or assistant-produced — newest first, grouped by conversation by default.
 *
 * **The URL is the source of truth for the query**, on the conversations library's
 * contract: `withComponentInputBinding()` binds `?name=`, `?view=` and `?source=` to the
 * inputs, {@link _applyQuery} is the only writer, and the loop cannot feed itself because
 * a navigation re-seeds `SearchInput`'s `linkedSignal`, whose debounce finds the text
 * unchanged and emits nothing. Both filters are served, so a search reaches every document
 * the caller holds rather than the window this screen happens to have paged to.
 */
@Component({
  selector: 'app-documents',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DocumentRow, EmptyState, ErrorPanel, Icon, RouterLink, SearchInput, Skeleton],
  providers: [DocumentLibraryStore],
  templateUrl: './documents.html',
  styleUrl: './documents.scss',
})
export class Documents {
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
   * Bound from `?view=` by `withComponentInputBinding()`.
   *
   * Anything other than the one value the screen writes reads as grouped — the
   * default frame `4j` draws — so a hand-edited `?view=list` cannot put the switch
   * and the listing out of step.
   */
  readonly view = input(false, {
    transform: (value: string | undefined) => value === VIEW_FLAT,
  });

  /**
   * Bound from `?source=` by `withComponentInputBinding()`, so it is named for the query
   * key rather than for what it means — {@link generatedOnly} is the readable half, as
   * {@link grouped} is for {@link view}.
   *
   * Same rule as {@link view}: anything but the one value the screen writes reads as
   * unfiltered, so a hand-edited `?source=uploaded` narrows nothing rather than asking
   * the server for something the switch cannot represent.
   */
  readonly source = input(false, {
    transform: (value: string | undefined) => value === SOURCE_GENERATED,
  });

  protected readonly library = inject(DocumentLibraryStore);

  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  private readonly _injector = inject(Injector);
  private readonly _document = inject(DOCUMENT);
  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef);
  // Optional, unlike the conversations screen's: the toolbar — and the field with
  // it — is withheld in the error branch here, so `required` would be a lie.
  private readonly _search = viewChild(SearchInput);
  // Optional for the same reason, and the control the filtered empty state hands focus
  // back to: it outlives the branch the button that moved focus lives in.
  private readonly _sourceSwitch = viewChild<ElementRef<HTMLInputElement>>('sourceSwitch');

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly chatRoute = CHAT_ROUTE;

  /**
   * A field, not a template literal: `provideCheckNoChangesConfig({ exhaustive: true })`
   * compares bindings between passes, and an array allocated in the template is a new
   * value every check.
   */
  protected readonly skeletonRows = Array.from({ length: SKELETON_ROWS });

  protected readonly grouped = computed(() => !this.view());

  protected readonly generatedOnly = computed(() => this.source());

  /**
   * Frame `4b`'s heading shape, naming the term.
   *
   * Read from the **store's** committed name, not from {@link name}: the store's copy is
   * the one the empty result belongs to.
   */
  protected readonly noMatchHeading = computed(() => `No documents match “${this.library.name()}”`);

  /**
   * The count and the busy state as one string, because they share one live region.
   *
   * Built here rather than interpolated beside an `@if` in the template, where the
   * formatter's line breaks become whitespace inside the announced text.
   */
  protected readonly announcement = computed(() => {
    if (this.library.isFirstLoad()) {
      return '';
    }

    const summary = this.library.countSummary();

    return this.library.loadingMore() ? `${summary}, loading more…` : summary;
  });

  /** The reader pressed Load more and focus has to survive the page landing. */
  private readonly _focusAfterPage = signal(false);

  /**
   * Conversations whose group the reader collapsed.
   *
   * Screen-local rather than store state: which groups are folded is view state that
   * should survive a filter change but die with the route, exactly like a scroll
   * position. A new `Set` per write, because `patchState`-style reference equality is
   * how signals decide whether anything changed. Held as collapsed ids — not expanded
   * ones — so every group starts expanded, including groups a filter reveals later.
   */
  private readonly _collapsed = signal<ReadonlySet<string>>(new Set());

  protected readonly trackGroup = (group: DocumentGroup): string => group.conversationId;

  constructor() {
    // The constructor is an injection context, which a signal-valued reactive method
    // requires. Handing over a computation — not the values — is what makes a deep link
    // issue one request carrying both filters, rather than an unfiltered request followed
    // by a filtered one. It is also what starts the listing.
    this.library.bindQuery(() => ({ name: this.name(), generatedOnly: this.source() }));

    // Focus, not state — the one thing a signal cannot express declaratively. Load more
    // removes itself when the last page lands, leaving the reader who pressed it standing
    // on a node that no longer exists and focus on `<body>`.
    effect(() => {
      const settled = !this.library.loadingMore();
      // Untracked, so clearing the request below cannot re-enter this effect, and so the
      // only thing it waits on is the page.
      const requested = untracked(this._focusAfterPage);

      if (!settled || !requested) {
        return;
      }

      this._focusAfterPage.set(false);

      // Still there and still focused: the reader is paging through, and moving focus off
      // the control they are pressing would be the worse defect.
      if (untracked(this.library.hasMore)) {
        return;
      }

      afterNextRender(() => this._focusLastRow(), { injector: this._injector });
    });
  }

  protected onLoadMore(): void {
    this.library.loadMore();
    // Only when a page actually started. The control carries `aria-disabled` rather than
    // `disabled`, so a press during a narrowing search still reaches this — and
    // `loadMore()` no-ops there, leaving a flag that would never be cleared.
    this._focusAfterPage.set(this.library.loadingMore());
  }

  protected isExpanded(conversationId: string): boolean {
    return !this._collapsed().has(conversationId);
  }

  protected toggleGroup(conversationId: string): void {
    const next = new Set(this._collapsed());
    if (!next.delete(conversationId)) {
      next.add(conversationId);
    }
    this._collapsed.set(next);
  }

  protected groupToggleLabel(group: DocumentGroup): string {
    return `${this.isExpanded(group.conversationId) ? 'Collapse' : 'Expand'} ${group.conversationName}`;
  }

  protected groupBodyId(conversationId: string): string {
    return `documents-group-${conversationId}`;
  }

  protected onSearched(term: string): void {
    const next = term.trim();

    this._applyQuery(
      // `null` removes the key rather than leaving `?name=` behind.
      { [NAME_PARAM]: next === '' ? null : next },
      shouldReplaceHistory(this.name(), next),
    );
  }

  protected toggleGrouped(): void {
    // Never replaces: on/off is the add-or-remove transition `shouldReplaceHistory`
    // exists to push at, so back undoes the toggle in both directions.
    this._applyQuery({ [VIEW_PARAM]: this.grouped() ? VIEW_FLAT : null }, false);
  }

  protected toggleGenerated(): void {
    // Never replaces, for the reason the view toggle does not.
    this._applyQuery({ [SOURCE_PARAM]: this.generatedOnly() ? null : SOURCE_GENERATED }, false);
  }

  protected clearFilter(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a list the reader then has to find again. The field is outside
    // every branch of the non-error template, so it is still connected.
    this._search()?.focusField();
  }

  /**
   * The filtered empty state's way out. It drops the name term too, because the label
   * promises every document and leaving a term behind would land the reader on the
   * no-matches card instead.
   */
  protected showEveryDocument(): void {
    this._applyQuery({ [SOURCE_PARAM]: null, [NAME_PARAM]: null }, false);
    // The button is inside the branch this navigation tears down, so focus would fall to
    // `<body>`; the switch it just turned off is where the state now lives.
    this._sourceSwitch()?.nativeElement.focus();
  }

  /**
   * Puts focus on the last row after the final page, since the control that was holding it
   * has gone.
   *
   * The download button rather than the file name: the name is a `<span>`, and the button
   * is the one control every row renders in both view modes. The filter field is the
   * fallback because it is the only control outside every branch of the listing.
   */
  private _focusLastRow(): void {
    // Only when focus actually fell through. A reader who clicked into the filter field
    // while the page was on the wire must not be yanked out of it.
    const active = this._document.activeElement;
    const fellThrough = active === null || active === this._document.body || !active.isConnected;

    if (!fellThrough) {
      return;
    }

    const actions = this._host.nativeElement.querySelectorAll<HTMLElement>('.document-row__action');
    const last = actions.item(actions.length - 1);

    if (last === null) {
      this._search()?.focusField();
      return;
    }

    last.focus();
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
      // Every filter has to survive a change to any other.
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
