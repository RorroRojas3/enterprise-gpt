import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
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
import {
  NAME_PARAM,
  SOURCE_GENERATED,
  SOURCE_PARAM,
  VIEW_FLAT,
  VIEW_PARAM,
  shouldReplaceHistory,
} from './documents-route';

/** How many skeleton rows stand in for the list while the first drain runs. */
const SKELETON_ROWS = 6;

/**
 * The documents library: every document the signed-in user holds in a conversation —
 * uploaded or assistant-produced — newest first, grouped by conversation by default.
 *
 * **The URL is the source of truth for the query**, on the conversations library's
 * contract: `withComponentInputBinding()` binds `?name=`, `?view=` and `?source=` to the
 * inputs, {@link _applyQuery} is the only writer, and the loop cannot feed itself because
 * a navigation re-seeds `SearchInput`'s `linkedSignal`, whose debounce finds the text
 * unchanged and emits nothing. Only the name filter is client-side; the origin is served,
 * for the reason `DocumentLibraryState.generatedOnly` records.
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
    // The constructor is an injection context, which a signal-valued reactive method
    // requires. Handing over the signals — not their values — is what makes a deep link
    // filter on its first render rather than flashing the unfiltered set. Binding the
    // origin is also what starts the listing: its first value runs the first drain.
    this.library.bindSource(this.source);
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
