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
import { canRetry } from '@core/errors/error-message';
import { CHAT_ROUTE } from '@core/navigation/route-paths';
import { DocumentGroup, DocumentLibraryStore } from '@state/documents/document-library-store';
import { EmptyState } from '@shared/components/empty-state/empty-state';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { SearchInput } from '@shared/components/search-input/search-input';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { DocumentRow } from '@components/documents/document-row';
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
 * The URL is the query's source of truth, on the conversations library's contract: the inputs
 * bind from it and {@link _applyQuery} is the only writer.
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
  /** Bound from `?name=`. Trimmed, so a hand-edited `?name=%20%20` means unfiltered. */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  /** Bound from `?view=`; anything but the value the screen writes reads as grouped. */
  readonly view = input(false, {
    transform: (value: string | undefined) => value === VIEW_FLAT,
  });

  /**
   * Bound from `?source=`, hence the name; {@link generatedOnly} is the readable half. Anything
   * but the value the screen writes reads as unfiltered.
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
  // Optional: the toolbar, field included, is withheld in the error branch.
  private readonly _search = viewChild(SearchInput);
  // Optional likewise; the filtered empty state hands focus back to it.
  private readonly _sourceSwitch = viewChild<ElementRef<HTMLInputElement>>('sourceSwitch');

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly chatRoute = CHAT_ROUTE;

  /** A field, not a template literal: exhaustive checkNoChanges would see a new array each pass. */
  protected readonly skeletonRows = Array.from({ length: SKELETON_ROWS });

  protected readonly grouped = computed(() => !this.view());

  protected readonly generatedOnly = computed(() => this.source());

  /** The store's committed term, not {@link name}: the one the empty result belongs to. */
  protected readonly noMatchHeading = computed(() => `No documents match “${this.library.name()}”`);

  /**
   * The count and busy state as one string for their shared live region; template interpolation
   * beside an `@if` would put the formatter's line breaks into the announced text.
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
   * Collapsed groups: view state that survives a filter change but dies with the route. Held as
   * collapsed ids, so groups a filter reveals later start expanded.
   */
  private readonly _collapsed = signal<ReadonlySet<string>>(new Set());

  protected readonly trackGroup = (group: DocumentGroup): string => group.conversationId;

  constructor() {
    this.library.bindQuery(() => ({ name: this.name(), generatedOnly: this.source() }));

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
  }

  protected onLoadMore(): void {
    this.library.loadMore();
    // Only if a page started: an aria-disabled press still lands here, and a flag set while
    // `loadMore()` no-ops would never be cleared.
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
    // Always pushes, so back undoes the toggle in both directions.
    this._applyQuery({ [VIEW_PARAM]: this.grouped() ? VIEW_FLAT : null }, false);
  }

  protected toggleGenerated(): void {
    // Always pushes, as the view toggle does.
    this._applyQuery({ [SOURCE_PARAM]: this.generatedOnly() ? null : SOURCE_GENERATED }, false);
  }

  protected clearFilter(): void {
    this.onSearched('');
    // The button removes itself once rows come back; the field sits outside every non-error
    // branch, so it is still there to take focus.
    this._search()?.focusField();
  }

  /** Drops the name term too: the label promises every document, not the no-matches card. */
  protected showEveryDocument(): void {
    this._applyQuery({ [SOURCE_PARAM]: null, [NAME_PARAM]: null }, false);
    // The button's branch is torn down; the switch it just turned off is where the state lives.
    this._sourceSwitch()?.nativeElement.focus();
  }

  /**
   * After the final page, focuses the last row's download button, the one control every row has
   * in both views; the filter field is the fallback.
   */
  private _focusLastRow(): void {
    // Only if focus fell through: a reader who clicked into the field meanwhile stays there.
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

  /** The single writer: every query change is a navigation, so the URL stays authoritative. */
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
