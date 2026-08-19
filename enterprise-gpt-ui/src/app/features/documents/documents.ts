import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
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
import { NAME_PARAM, VIEW_FLAT, VIEW_PARAM, shouldReplaceHistory } from './documents-route';

/** How many skeleton rows stand in for the list while the first drain runs. */
const SKELETON_ROWS = 6;

/**
 * The documents library, frame `4j` (US-1002/US-1003): every document the signed-in
 * user has uploaded, newest first, grouped by conversation by default with a flat
 * listing behind `?view=flat`.
 *
 * **The URL is the source of truth for the query**, on the conversations library's
 * contract: `withComponentInputBinding()` binds `?name=` and `?view=` to the inputs,
 * {@link _applyQuery} is the only writer, and the loop cannot feed itself because a
 * navigation re-seeds `SearchInput`'s `linkedSignal`, whose debounce finds the text
 * unchanged and emits nothing. The one structural difference: the filter is
 * **client-side** — `bindQuery` narrows rows the store already holds, so typing
 * navigates but never issues a request.
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

  protected readonly library = inject(DocumentLibraryStore);

  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  // Optional, unlike the conversations screen's: the toolbar — and the field with
  // it — is withheld in the error branch here, so `required` would be a lie.
  private readonly _search = viewChild(SearchInput);

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

  /**
   * Frame `4b`'s heading shape, naming the term.
   *
   * Read from the **store's** committed `query()`, not from {@link name}: the store's
   * copy is the one the empty result belongs to.
   */
  protected readonly noMatchHeading = computed(
    () => `No documents match “${this.library.query()}”`,
  );

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
    this.library.load();
    // The constructor is an injection context, which a signal-valued reactive method
    // requires. Handing over the signal — not its value — is what makes a deep link
    // filter on its first render rather than flashing the unfiltered set.
    this.library.bindQuery(this.name);
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

  protected clearFilter(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a list the reader then has to find again. The field is outside
    // every branch of the non-error template, so it is still connected.
    this._search()?.focusField();
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
      // The term has to survive the view toggle, and vice versa.
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
