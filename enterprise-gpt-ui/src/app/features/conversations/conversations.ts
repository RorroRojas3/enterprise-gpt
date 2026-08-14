import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ConversationDto } from '@domain/api/conversation';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { canRetry } from '@core/errors/error-message';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { SearchInput } from '@shared/form/search-input/search-input';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { ConversationLibraryStore } from './conversation-library-store';
import { NAME_PARAM, shouldReplaceHistory } from './conversations-route';

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
 * Frame `4a` also draws a favourites filter (US-703), row checkboxes and a bulk bar
 * (US-704), a "Showing N of M" counter and a Load more control (US-702), a project
 * chip (US-307), and a per-row star and kebab. None of them are here: this story is
 * the screen and its search. What the frame deliberately does *not* draw is a sort
 * control, and that stays absent until US-706 gives the server one to honour.
 */
@Component({
  selector: 'app-conversations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DataTable, EmptyState, ErrorPanel, RouterLink, SearchInput, TableCell],
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

  protected readonly library = inject(ConversationLibraryStore);

  private readonly _models = inject(ModelCatalogStore);
  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  private readonly _search = viewChild.required(SearchInput);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

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
    // that outlives it. Handing over the signal — not its value — is what makes a
    // deep link issue one request carrying the term, rather than an unfiltered
    // request followed by a filtered one.
    this.library.bindQuery(this.name);
  }

  protected onSearched(term: string): void {
    const next = term.trim();

    void this._router.navigate([], {
      relativeTo: this._route,
      // `null` removes the key rather than leaving `?name=` behind.
      queryParams: { [NAME_PARAM]: next === '' ? null : next },
      // US-703's favourites parameter has to survive a search, and vice versa.
      queryParamsHandling: 'merge',
      replaceUrl: shouldReplaceHistory(this.name(), next),
    });
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a list the reader then has to find again. The field is outside
    // every branch of the template, so it is always connected.
    this._search().focusField();
  }
}
