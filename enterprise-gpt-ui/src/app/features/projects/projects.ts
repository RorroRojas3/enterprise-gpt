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
import { ProjectSummaryDto } from '@shared/models/api/project.model';
import { formatRelativeTime } from '@shared/utils/format/relative-time';
import {
  DEFAULT_PROJECT_SORT,
  NAME_PARAM,
  PROJECT_SORTS,
  SORT_PARAM,
  shouldReplaceHistory,
  sortKey,
  toProjectSort,
} from '@shared/utils/navigation/projects-route';
import { canRetry } from '@core/errors/error-message';
import { PROJECTS_ROUTE } from '@core/navigation/route-paths';
import { ProjectActionsStore } from '@state/project-actions-store';
import { ProjectListStore } from '@state/projects/project-list-store';
import { EmptyState } from '@shared/components/empty-state/empty-state';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { SearchInput } from '@shared/components/search-input/search-input';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';
import { ProjectCard } from '@components/projects/project-card';

/** How many skeleton cards stand in for the grid while the first page loads. */
const SKELETON_CARDS = 6;

/**
 * The URL is the query's source of truth: `?name=` and `?sort=` bind to inputs that drive the
 * store, and only user gestures navigate. The loop cannot feed itself while `SearchInput`'s
 * `[value]` stays one-way and nothing navigates from an effect over `name()`.
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
  // The shell's `<main>` clips its overflow, so a routed screen scrolls itself.
  host: { class: 'd-flex flex-column gap-3 p-3 p-md-4 overflow-y-auto' },
  templateUrl: './projects.html',
})
export class Projects {
  /** Trimmed: the router writes `undefined` when the key leaves, and `?name=%20` is unfiltered. */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  /** Resolved to an option, so the select and the request agree on a hand-edited value. */
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

  protected readonly sortOptions = PROJECT_SORTS;

  /** Names the store's term, which the rendered cards belong to, not {@link name}'s. */
  protected readonly noMatchHeading = computed(() => `No projects match “${this.projects.name()}”`);

  /**
   * The clock is sampled once per result set: a template `Date.now()` changes every pass, which
   * exhaustive checkNoChanges rejects. Labels are minute-granular, so nothing visible is lost.
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
   * A string, not a commands array: a fresh array literal fails the signal input's `Object.is`
   * every pass and dirties every card in the grid.
   */
  protected linkFor(project: ProjectSummaryDto): string {
    return `${PROJECTS_ROUTE}/${project.id}`;
  }

  /**
   * Armed at the gesture: the removal arrives later as `projectEvents.deleted`, with nothing to
   * say the reader caused it.
   */
  private readonly _focusAfterEmptied = signal(false);

  /** The reader pressed Load more and focus has to survive the page landing. */
  private readonly _focusAfterPage = signal(false);

  constructor() {
    // A computation, so a deep link fires one request rather than unfiltered then filtered.
    this.projects.bindQuery(() => ({ name: this.name(), sort: this.sort() }));

    // An emptied grid leaves focus on `<body>`; the search field is the only control outside
    // every branch of the template.
    effect(() => {
      const emptied = this.projects.entities().length === 0;

      // Disarmed once non-empty, so a flag left by a cancelled delete cannot fire on a later,
      // unrelated emptying.
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

    // Load more removes itself when the last page lands, dropping focus on `<body>`.
    effect(() => {
      const settled = !this.projects.loadingMore();
      // Untracked, so clearing it below cannot re-enter and only the page is awaited.
      const requested = untracked(this._focusAfterPage);

      if (!settled || !requested) {
        return;
      }

      this._focusAfterPage.set(false);

      // Still there: moving focus off the control being pressed would be the worse defect.
      if (untracked(this.projects.hasMore)) {
        return;
      }

      afterNextRender(() => this._focusLastCard(), { injector: this._injector });
    });
  }

  protected onLoadMore(): void {
    this.projects.loadMore();
    // Only if a page started: `aria-disabled` lets a press through mid-search, where
    // `loadMore()` no-ops and the flag would never clear.
    this._focusAfterPage.set(this.projects.loadingMore());
  }

  /** Arms the focus fixup in case this is the last card. */
  protected beginDelete(project: ProjectSummaryDto): void {
    this._focusAfterEmptied.set(this.projects.entities().length === 1);
    this.actions.beginDelete(project);
  }

  /** Under an active search a rename can evict the last card, stranding focus in its kebab. */
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

  /** Checked against the offered set: anything else is a tampered DOM and would earn a 400. */
  protected onSorted(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const next = this.sortOptions.find((option) => option.value === value);

    if (next === undefined || next.value === this.sort().value) {
      return;
    }

    // The default's key is removed, so the rule below compares the values the URL will hold.
    const previousKey = sortKey(this.sort());
    const nextKey = sortKey(next);

    this._applyQuery(
      { [SORT_PARAM]: nextKey === '' ? null : nextKey },
      // A closed `<select>` fires `change` per arrow key, so only turning an order on or off
      // pushes; moving between two orders replaces.
      shouldReplaceHistory(previousKey, nextKey),
    );
  }

  protected clearSearch(): void {
    this.onSearched('');
    // The button vanishes once cards return; the field is outside every template branch.
    this._search().focusField();
  }

  /** The name link, not the kebab: it is the card's heading, so a screen reader hears where. */
  private _focusLastCard(): void {
    // Only if focus fell through: a reader who clicked into search mid-page keeps it.
    const active = this._document.activeElement;
    const fellThrough = active === null || active === this._document.body || !active.isConnected;

    if (!fellThrough) {
      return;
    }

    const cards = this._host.nativeElement.querySelectorAll<HTMLElement>('[data-row-anchor]');
    const last = cards.item(cards.length - 1);

    if (last === null) {
      this._search().focusField();
      return;
    }

    last.focus();
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

  /** The single writer: every query change is a navigation, so the URL stays authoritative. */
  private _applyQuery(queryParams: Params, replaceUrl: boolean): void {
    void this._router.navigate([], {
      relativeTo: this._route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
