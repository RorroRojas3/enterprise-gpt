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
import { ActivatedRoute, Params, Router } from '@angular/router';
import { ProjectSummaryDto } from '@domain/api/project';
import { formatRelativeTime } from '@domain/format/relative-time';
import { PROJECTS_ROUTE } from '@core/auth/auth-routes';
import { canRetry } from '@core/errors/error-message';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { ProjectCard } from '@shared/card/project-card/project-card';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { SearchInput } from '@shared/form/search-input/search-input';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { ProjectListStore } from './project-list-store';
import {
  DEFAULT_PROJECT_SORT,
  NAME_PARAM,
  PROJECT_SORTS,
  SORT_PARAM,
  shouldReplaceHistory,
  sortKey,
  toProjectSort,
} from './projects-route';

/** How many skeleton cards stand in for the grid while the first drain runs. */
const SKELETON_CARDS = 6;

/**
 * The projects grid, frame `4c` (US-901).
 *
 * **The URL is the source of truth for the query**, on exactly the contract the
 * conversations library set: `withComponentInputBinding()` binds `?name=` to
 * {@link name}, which drives the store; the `searched` handler is the only writer; and
 * the loop cannot feed itself because a navigation re-seeds `SearchInput`'s
 * `linkedSignal`, whose debounce then finds the text unchanged and emits nothing. Two
 * rules keep that true: bind `[value]` one-way, never `[(value)]`, and never navigate
 * from an effect over `name()` — only from the user's own gesture.
 *
 * **The sort is a second URL parameter on the same contract (US-902).** `?sort=` names
 * one of {@link PROJECT_SORTS} and the store turns it into the API's `sort=`/`dir=` pair.
 * History follows the search term's rule rather than pushing unconditionally: turning an
 * order **on** or **off** pushes, so Back returns the reader to the grid as it was, while
 * moving between two orders replaces — which matters because a closed `<select>` fires
 * `change` on every arrow key, and three pushes for one gesture would bury the unsorted
 * grid under entries nobody chose.
 *
 * Two departures from frame `4c` and `4d` are deliberate, and both are the board being
 * older than the API: the select is **never disabled** past the load ceiling, since
 * US-706 made the order the server's rather than a comparator over a partial set; and the
 * fourth option reads "Favourites first" where the board says "Pinned".
 */
@Component({
  selector: 'app-projects',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    EmptyState,
    ErrorPanel,
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    ProjectCard,
    SearchInput,
    Skeleton,
  ],
  providers: [ProjectListStore],
  templateUrl: './projects.html',
  styleUrl: './projects.scss',
})
export class Projects {
  /**
   * Bound from `?name=` by `withComponentInputBinding()`.
   *
   * Trimmed in the transform because the router writes `undefined` when the key leaves
   * the URL, and a hand-edited `?name=%20%20` has to mean unfiltered.
   */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  /**
   * Bound from `?sort=` by `withComponentInputBinding()`.
   *
   * Resolved to an option rather than kept as a string, so the select and the request
   * cannot disagree about what a hand-edited value means.
   */
  readonly sort = input(DEFAULT_PROJECT_SORT, { transform: toProjectSort });

  protected readonly projects = inject(ProjectListStore);
  protected readonly actions = inject(ProjectActionsStore);

  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  private readonly _injector = inject(Injector);
  private readonly _document = inject(DOCUMENT);
  private readonly _host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly _search = viewChild.required(SearchInput);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly skeletonCards = Array.from({ length: SKELETON_CARDS });

  /** Frame `4c`'s four options, rendered in the order the board lists them. */
  protected readonly sortOptions = PROJECT_SORTS;

  /**
   * Frame `4b`'s heading, naming the term.
   *
   * Read from the **store**, not from {@link name}: the store's copy is the one the
   * rendered cards belong to, so the heading cannot name a term whose results have not
   * arrived yet.
   */
  protected readonly noMatchHeading = computed(() => `No projects match “${this.projects.name()}”`);

  /**
   * One "Updated …" label per card, computed once per result set.
   *
   * A `Date.now()` read from a template binding would be a fresh value on every change
   * detection pass, which `provideCheckNoChangesConfig({ exhaustive: true })` compares
   * between passes — and would recompute every label on every unrelated render. The
   * clock is sampled once per drain instead; labels are minute-granular at their
   * finest, so nothing visible is lost.
   */
  private readonly _updatedLabels = computed(() => {
    const now = Date.now();

    return new Map(
      this.projects
        .entities()
        .map((project) => [project.id, formatRelativeTime(project.dateModified, now)]),
    );
  });

  protected readonly trackId = (project: ProjectSummaryDto): string => project.id;

  protected updatedLabel(project: ProjectSummaryDto): string {
    return this._updatedLabels().get(project.id) ?? '';
  }

  /**
   * The card's link, as a string rather than a commands array.
   *
   * `ProjectCard.link` is a signal `input()`, so a fresh `[route, id]` literal fails
   * `Object.is` on every change-detection pass — writing the input, marking every card
   * dirty and re-running `RouterLink`'s href update. At the 500-project ceiling this
   * store deliberately materialises, that is OnPush defeated across the whole grid.
   */
  protected linkFor(project: ProjectSummaryDto): string {
    return `${PROJECTS_ROUTE}/${project.id}`;
  }

  /**
   * A delete the reader confirmed, so focus can be caught if its card was the last one.
   *
   * Armed at the gesture rather than inferred from the grid emptying: the removal is
   * confirmed elsewhere — `ProjectActionsStore.confirmDelete` announces it as
   * `projectEvents.deleted` — so by the time the card goes there is nothing left to
   * tell this screen that the reader was the cause.
   */
  private readonly _focusAfterEmptied = signal(false);

  constructor() {
    // The constructor is an injection context, which a signal-valued reactive method
    // requires: it binds the subscription to this screen rather than to the injector
    // that outlives it. Handing over a computation — not the value — is what makes a
    // deep link one drain rather than an unfiltered one followed by a filtered one.
    this.projects.bindQuery(() => ({ name: this.name(), sort: this.sort() }));

    // Focus, not state — the one thing a signal cannot express declaratively. A card
    // removed by a delete can be the node the reader was standing on, and an emptied
    // grid leaves focus on `<body>`; the search field is the only control outside every
    // branch of the template.
    effect(() => {
      const emptied = this.projects.entities().length === 0;

      // Disarmed as soon as the grid is not empty, which is what stops a flag left over
      // from a cancelled or failed delete from firing against some later, unrelated
      // emptying — a search with no matches, or another tab's delete.
      if (!emptied) {
        this._focusAfterEmptied.set(false);

        return;
      }

      if (!untracked(this._focusAfterEmptied)) {
        return;
      }

      this._focusAfterEmptied.set(false);
      afterNextRender(() => this._focusField(), { injector: this._injector });
    });
  }

  /**
   * Opens the delete confirmation, arming the focus fixup in case this is the last card.
   */
  protected beginDelete(project: ProjectSummaryDto): void {
    this._focusAfterEmptied.set(this.projects.entities().length === 1);
    this.actions.beginDelete(project);
  }

  /**
   * Opens the edit dialog, arming the same fixup when a rename could evict the card.
   *
   * It can: under an active search the store drops a row renamed out of the term, so
   * renaming the last matching project empties the grid and leaves focus on the kebab
   * item inside a card that no longer exists. Without a term nothing can leave, so the
   * flag stays down.
   */
  protected beginEdit(project: ProjectSummaryDto): void {
    this._focusAfterEmptied.set(this.projects.hasTerm() && this.projects.entities().length === 1);
    this.actions.beginEdit(project);
  }

  protected onSearched(term: string): void {
    const next = term.trim();

    this._applyQuery(
      // `null` removes the key rather than leaving `?name=` behind.
      { [NAME_PARAM]: next === '' ? null : next },
      shouldReplaceHistory(this.name(), next),
    );
  }

  /**
   * Records the chosen order in the URL, which is what re-runs the query.
   *
   * The value is checked against the offered set rather than trusted: a `change` event
   * carrying anything else can only come from a tampered DOM, and sending it would take
   * a 400 the reader could not explain. `Paginator` guards its page-size select the same
   * way.
   */
  protected onSorted(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const next = this.sortOptions.find((option) => option.value === value);

    if (next === undefined || next.value === this.sort().value) {
      return;
    }

    // `null` removes the key rather than spelling out the default the reader gets from a
    // bare `/projects`, so the rule below reads the two values the URL will actually hold.
    const previousKey = sortKey(this.sort());
    const nextKey = sortKey(next);

    this._applyQuery(
      { [SORT_PARAM]: nextKey === '' ? null : nextKey },
      // The search term's rule, for the same reason: a closed `<select>` fires `change`
      // on every arrow key, so pushing unconditionally would bury the unsorted grid under
      // three entries nobody chose. Turning an order **on** or **off** pushes — Back
      // returns to the list as it was — while moving between two orders replaces.
      shouldReplaceHistory(previousKey, nextKey),
    );
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself the moment cards come back, and focus would fall to
    // `<body>` inside a grid the reader then has to find again. The field is outside
    // every branch of the template, so it is always connected.
    this._search().focusField();
  }

  /** Moves focus to the search field when the last card leaves the grid. */
  private _focusField(): void {
    const active = this._document.activeElement;

    if (
      active === null ||
      active === this._document.body ||
      !active.isConnected ||
      // A card the reader was standing on is gone, but its host may linger for a frame.
      !this._host.nativeElement.contains(active)
    ) {
      this._search().focusField();
    }
  }

  /**
   * The single writer. Every query change is a navigation and nothing writes the store
   * directly, which is what keeps the URL authoritative and the back button working
   * without a line of code.
   */
  private _applyQuery(queryParams: Params, replaceUrl: boolean): void {
    void this._router.navigate([], {
      relativeTo: this._route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
