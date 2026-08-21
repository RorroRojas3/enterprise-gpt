import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  untracked,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { PermissionDto, UserDto } from '@domain/api/user';
import { canRetry } from '@core/errors/error-message';
import { SessionStore } from '@core/session/session-store';
import { SELF_DEACTIVATE_MESSAGE } from '@core/users/self-action-messages';
import { UserActionsStore } from '@core/users/user-actions-store';
import { AvatarInitials } from '@shared/avatar/avatar-initials/avatar-initials';
import { PermissionBadge } from '@shared/badge/permission-badge/permission-badge';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { Paginator } from '@shared/data/paginator/paginator';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { SearchInput } from '@shared/form/search-input/search-input';
import { Icon } from '@shared/icon/icon';
import { Tooltip } from '@shared/overlay/tooltip/tooltip';
import { AdminUsersStore } from './admin-users-store';
import { DeactivateUserDialog } from './deactivate-user-dialog';
import { UserEditPanel } from './user-edit-panel';
import { UserFormDialog } from './user-form-dialog';
import {
  ANY_PERMISSION_LABEL,
  NAME_PARAM,
  ORDER_EXPLANATION,
  PAGE_PARAM,
  PERMISSION_PARAM,
  SIZE_PARAM,
  USER_PAGE_SIZES,
  shouldReplaceHistory,
  toPage,
  toPageSize,
  toPermissionFilter,
} from './admin-users-route';

/**
 * The administration user directory, frame `5a` (US-1201).
 *
 * **The URL is the source of truth for the query** — `?name=`, `?page=`, `?size=` and
 * `?permission=`.
 * The field and the pager are views of it, and the store caches the rows they produced,
 * so there is exactly one writer ({@link _applyQuery}) and one reader path:
 * `withComponentInputBinding()` binds the parameters to inputs, which drive the store.
 * The back button restoring the previous page is then a property of the router rather
 * than code.
 *
 * The loop cannot feed itself. A navigation changes {@link name}, which re-seeds
 * `SearchInput`'s `linkedSignal`; its debounce compares the new text against the value
 * it already holds, finds them equal, and emits nothing. Two rules keep that true: bind
 * `[value]` one-way, never `[(value)]`, and never navigate from an effect over the
 * bound inputs — only from the user's own gesture.
 *
 * **The second rule has exactly one exception, and it is load-bearing rather than a
 * lapse.** A `?permission=` naming a permission the directory does not offer has to be
 * repaired, and there is no gesture to hang that on: the reader followed a link. The
 * effect that drops the parameter is safe because it terminates by construction — it
 * fires only while {@link permission} is non-null and the catalog has disowned it, and
 * the navigation it performs sets {@link permission} to null, which is the condition's
 * own negation. It also touches a parameter no debounced control is bound to, so the
 * feedback path the rule exists to prevent is not on it.
 *
 * Two controls frame `5a` draws are deliberately absent. There is **no sort control** —
 * the endpoint accepts one since US-706, so this is a follow-up on this tab rather than a
 * limit of the API, and the order is stated meanwhile. And there is **no row-selection
 * column**, because no endpoint acts on more than one user and a checkbox that can only
 * ever select is not a control.
 *
 * The board's third absent control, the **"Permission: any" filter**, is built (US-1206):
 * US-1205 put `permissionId=` on the endpoint, so it narrows the whole directory rather
 * than the loaded page, and frame `5a`'s "Filters loaded rows only." caption — which
 * described the interim it replaced — is deliberately not rendered.
 *
 * Frame `5a`'s **Last active** column is absent too: `UserDto` carries no timestamp of
 * any kind, and `dateModified` moves only when an administrator edits the row, never
 * when the user signs in. Tracked as an interim behaviour rather than filled with a
 * plausible-looking value.
 */
@Component({
  selector: 'app-admin-users',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AvatarInitials,
    DataTable,
    DeactivateUserDialog,
    EmptyState,
    ErrorPanel,
    Icon,
    Paginator,
    PermissionBadge,
    SearchInput,
    TableCell,
    Tooltip,
    UserEditPanel,
    UserFormDialog,
  ],
  providers: [AdminUsersStore],
  templateUrl: './admin-users.html',
  styleUrl: './admin-users.scss',
})
export class AdminUsers {
  /**
   * Bound from `?name=` by `withComponentInputBinding()`.
   *
   * Trimmed in the transform because the router writes `undefined` when the key leaves
   * the URL, and a hand-edited `?name=%20%20` has to mean unfiltered.
   */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  readonly page = input(1, { transform: toPage });
  readonly size = input(USER_PAGE_SIZES[0] as number, { transform: toPageSize });

  /**
   * Bound from `?permission=` by `withComponentInputBinding()` (US-1206).
   *
   * The transform only normalizes; whether the id names anything is settled against the
   * catalog in {@link permissionFilter}, because only the server knows what exists.
   */
  readonly permission = input<string | null, string | undefined>(null, {
    transform: toPermissionFilter,
  });

  protected readonly users = inject(AdminUsersStore);
  protected readonly actions = inject(UserActionsStore);

  private readonly _session = inject(SessionStore);
  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  private readonly _search = viewChild.required(SearchInput);
  private readonly _permissionField =
    viewChild.required<ElementRef<HTMLSelectElement>>('permissionField');

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly ORDER_EXPLANATION = ORDER_EXPLANATION;
  protected readonly SELF_DEACTIVATE_MESSAGE = SELF_DEACTIVATE_MESSAGE;
  protected readonly ANY_PERMISSION_LABEL = ANY_PERMISSION_LABEL;
  protected readonly pageSizes = USER_PAGE_SIZES;

  /**
   * Whether the permission catalog has answered — with rows, with none, or with a failure.
   *
   * Read from `permissionsLoaded` rather than from `permissions().length > 0`, which is
   * what the root store's own cache guard uses: there a `200 []` costing a redundant
   * refetch is harmless, here it would be a fixed point. The request is withheld until
   * this is true, so a catalog that answered with nothing would leave the screen waiting
   * on a load that had already happened — a permanent skeleton, no request, no error and
   * no way out. A failure settles it as surely as rows do, because the question is
   * whether the id can be checked at all.
   */
  private readonly _catalogSettled = computed(
    () => this.actions.permissionsLoaded() || this.actions.permissionsError() !== null,
  );

  /** The permission `?permission=` names, once the catalog vouches for it. */
  protected readonly selectedPermission = computed<PermissionDto | null>(() => {
    const id = this.permission();

    return id === null
      ? null
      : (this.actions.permissions().find((permission) => permission.id === id) ?? null);
  });

  /**
   * The `permissionId` the request carries.
   *
   * An id the catalog does not list reads as no filter at all, so the select can never
   * show "Permission: any" while the request underneath it is narrowed — the invariant
   * `?size=` follows, enforced against a set that only exists at run time. The exception
   * is a catalog that failed to load: nothing can vouch for anything then, and refusing
   * to send a filter the reader explicitly asked for would be a worse answer than the
   * server's own 400.
   */
  protected readonly permissionFilter = computed(() => {
    const id = this.permission();

    if (id === null || this.actions.permissionsError() !== null) {
      return id;
    }

    return this.selectedPermission()?.id ?? null;
  });

  /**
   * The filter the **rendered rows** were narrowed by, when no option can name it.
   *
   * Reachable only through a failed catalog, which is the one case
   * {@link permissionFilter} sends an unvouched-for id in. The select renders it as an
   * option of its own rather than silently displaying "Permission: any": a control that
   * shows no filter while the request underneath it is narrowed is the exact contradiction
   * this screen's URL rules exist to prevent — and without an option to move off,
   * re-selecting "any" fires no `change` event, so the reader would have no way back to
   * the unfiltered directory but the address bar.
   *
   * Asked as "is the applied filter unnameable" rather than "did the catalog error",
   * because those come apart while a failed catalog is being **retried**: the error is
   * cleared the moment the retry starts, and keying on it would flip the control back to
   * "any" for the duration of a request whose rows are still filtered. It also cannot
   * render a second option for an id `@for` already lists.
   */
  protected readonly unnamedFilter = computed(() =>
    this.users.permissionId() !== null && this._appliedPermissionName() === null
      ? this.users.permissionId()
      : null,
  );

  /**
   * True while a permission-filtered link waits for the catalog to say whether its id
   * exists. The table shows its skeleton meanwhile rather than a page of the unfiltered
   * directory it would have to replace a moment later.
   */
  protected readonly awaitingPermissions = computed(
    () => this.permission() !== null && !this._catalogSettled(),
  );

  /**
   * The skeleton covers the catalog wait as well as the directory's own first load.
   *
   * Guarded on there being no rows for the same reason `isFirstLoad` is: retrying a failed
   * catalog clears `permissionsError` before the rows arrive, and a directory already on
   * screen must not be replaced by a skeleton while that settles.
   */
  protected readonly isTableLoading = computed(
    () =>
      this.users.isFirstLoad() ||
      (this.awaitingPermissions() && this.users.entities().length === 0),
  );

  /**
   * Frame `5b`'s empty-state heading, naming whichever filters produced it.
   *
   * Read from the **store**, not from {@link name} and {@link permission}: the store's
   * copy is the one the rendered rows belong to, so the heading cannot name a term whose
   * results have not arrived yet.
   *
   * Three arms rather than one, because the directory can now be narrowed two ways and a
   * heading that blames only the term would send a reader to clear a search that was
   * working. The permission is named from the catalog rather than by id — an id is not a
   * thing an administrator recognizes — and falls back to the neutral wording if the row
   * has since gone.
   */
  protected readonly noMatchHeading = computed(() => {
    const term = this.users.name().trim();

    if (!this._filteredByPermission()) {
      return `No users match “${term}”`;
    }

    // Whether the permission is *on* and whether it can be *named* are different
    // questions, and only the first may choose the arm. Keying the branch off the name
    // would drop a directory filtered by a permission the catalog cannot name — reachable
    // whenever that request failed — into the term arm, which renders `No users match “”`
    // over an empty search box.
    const name = this._appliedPermissionName();
    const held = name === null ? 'this permission' : `“${name}”`;

    return term === '' ? `No users hold ${held}` : `No users match “${term}” and hold ${held}`;
  });

  /** What the empty state says the filters cover, matched to the arms above. */
  protected readonly noMatchMessage = computed(() => {
    const filteredByTerm = this.users.name().trim() !== '';

    if (filteredByTerm && this._filteredByPermission()) {
      return 'Search covers first name, last name and email address, and the permission narrows it further.';
    }

    return this._filteredByPermission()
      ? 'Only users holding this permission are listed.'
      : 'Search covers first name, last name and email address.';
  });

  /** The clearing action's label, so it never offers to clear a filter that is not on. */
  protected readonly clearFiltersLabel = computed(() => {
    const filteredByTerm = this.users.name().trim() !== '';

    if (filteredByTerm && this._filteredByPermission()) {
      return 'Clear filters';
    }

    return this._filteredByPermission() ? 'Clear filter' : 'Clear search';
  });

  /**
   * Whether the **rendered rows** were narrowed by a permission.
   *
   * The store's copy, not the input: it is the one the rows on screen belong to, so the
   * copy cannot describe a filter whose results have not arrived. This is the same fact
   * `AdminUsersStore.hasNoMatches` branches on, which is what keeps the empty state and
   * the condition that shows it from disagreeing.
   */
  private readonly _filteredByPermission = computed(() => this.users.permissionId() !== null);

  /**
   * The name of the permission the rendered rows were filtered by, or `null` when there is
   * none — or when the catalog cannot name the one there is.
   */
  private readonly _appliedPermissionName = computed(() => {
    const id = this.users.permissionId();

    if (id === null) {
      return null;
    }

    return this.actions.permissions().find((permission) => permission.id === id)?.name ?? null;
  });

  /**
   * Fields rather than template literals. `provideCheckNoChangesConfig({ exhaustive:
   * true })` compares bindings between passes, and an array or an arrow allocated in the
   * template is a new value on every check.
   */
  protected readonly columns: readonly TableColumn<UserDto>[] = [
    { key: 'user', header: 'User', width: 'minmax(0, 1.2fr)', mobile: 'title' },
    { key: 'email', header: 'Email', width: 'minmax(0, 1.3fr)', mobile: 'subtitle' },
    { key: 'permissions', header: 'Permissions', width: 'minmax(0, 1.7fr)', mobile: 'badges' },
    // headerHidden rather than an empty header: the column still needs a name for a
    // screen reader listing the table's columns, and the board draws no label here.
    {
      key: 'actions',
      header: 'Actions',
      headerHidden: true,
      width: '160px',
      align: 'end',
      mobile: 'actions',
    },
  ];

  protected readonly trackKey = (user: UserDto): string => user.id;
  protected readonly rowLabel = (user: UserDto): string => this.displayName(user);

  /**
   * The name to show, falling back to the email address.
   *
   * `fullName` is legitimately empty for an account Microsoft Graph gave neither a
   * `givenName` nor a `surname` — the API coalesces both to empty strings — and a blank
   * cell beside a filled avatar reads as a rendering fault rather than as missing
   * directory data.
   */
  protected displayName(user: UserDto): string {
    return user.fullName.trim() === '' ? user.email : user.fullName;
  }

  /**
   * Whether a row is the signed-in administrator, for frame `5d`'s `(you)` marker.
   *
   * Matched on the object id, which `UserDto.id` and the session's own row share —
   * the API keys user rows on the Entra `oid` directly.
   */
  protected isSelf(user: UserDto): boolean {
    return user.id === this._session.user()?.id;
  }

  /** The id of a row's inline refusal, which its Deactivate control is described by. */
  protected refusalId(user: UserDto): string {
    return `admin-users-refusal-${user.id}`;
  }

  /**
   * Deactivates the confirmed row.
   *
   * The row leaves at once and the undo travels to the request's store as a callback:
   * that store is root-scoped so a tab change cannot tear the request down mid-flight,
   * and it cannot reach back into this route-scoped one, because `core/` may not import
   * from `features/`.
   */
  protected onConfirmDeactivate(): void {
    const target = this.actions.deactivateTarget();
    if (target === null) {
      return;
    }

    const removed = this.users.takeRow(target.id);
    this.actions.confirmDeactivate(() => this.users.restoreRow(removed));
  }

  constructor() {
    // The constructor is an injection context, which a signal-valued reactive method
    // requires: it binds the subscription to this screen rather than to the injector
    // that outlives it. Handing over a computation — not the values — is what makes a
    // deep link issue one request carrying all three parameters, rather than a default
    // request followed by the real one.
    this.users.bindQuery(() =>
      // `null` withholds the request rather than sending an unfiltered one: a link naming
      // a permission cannot become a query until the catalog says whether that permission
      // exists, and the whole directory on screen for one paint is a worse answer than a
      // skeleton.
      this.awaitingPermissions()
        ? null
        : {
            name: this.name(),
            page: this.page(),
            pageSize: this.size(),
            permissionId: this.permissionFilter(),
          },
    );

    // The filter needs the catalog to render its options at all, so it is asked for on
    // mount rather than when a form opens. Cached in the root store, so the edit panel
    // and the checklist reuse this copy instead of fetching their own.
    this.actions.ensurePermissions();

    // A link naming a permission the directory does not offer — deactivated since it was
    // shared, or hand-edited — is repaired rather than refused: the parameter is dropped
    // so the select and the URL agree on "any", which is the same rule `?size=` follows.
    // `replaceUrl`, because the reader did not author this URL and Back must not return
    // to it. It cannot loop: the navigation clears the input the condition reads.
    // Note the asymmetry: this deliberately does not fire when the catalog *failed*, since
    // nothing can disown an id then. That case keeps the filter and surfaces it through
    // `unnamedFilter` instead.
    effect(() => {
      if (
        this.permission() !== null &&
        this._catalogSettled() &&
        this.permissionFilter() === null
      ) {
        // `untracked`: the router is imperative code that reads signals of its own, and an
        // effect must not take a dependency on whatever it happens to touch.
        untracked(() => this._applyQuery({ [PERMISSION_PARAM]: null }, true));
      }
    });

    // `UserActionsStore` is root-scoped and outlives this route, while a refusal is about
    // one attempt on one screen. Without this, returning to the tab would re-announce it.
    this.actions.clearSelfDeactivateRefusal();
  }

  protected onSearched(term: string): void {
    const next = term.trim();

    this._applyQuery(
      {
        // `null` removes the key rather than leaving `?name=` behind.
        [NAME_PARAM]: next === '' ? null : next,
        // A narrowed search almost never has as many pages as the one before it, so
        // staying on page 4 would answer a new term with an empty table.
        [PAGE_PARAM]: null,
      },
      shouldReplaceHistory(this.name(), next),
    );
  }

  protected onPageChanged(page: number): void {
    // Never replaces. A page change is a discrete transition rather than the refinement
    // of one, so back returns to the page the reader came from.
    this._applyQuery({ [PAGE_PARAM]: page === 1 ? null : page }, false);
  }

  protected onPermissionFiltered(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;

    // Guarded against the catalog rather than trusted: the option list is rendered from
    // it, so anything else reaching here is a value the directory cannot filter by.
    const next =
      value === ''
        ? null
        : // Falls back to the unnamed filter rather than to null: it is the one option in
          // the list the catalog cannot vouch for, and resolving it to "no filter" would
          // make re-selecting what is already selected clear the filter instead of
          // no-opping.
          (this.actions.permissions().find((permission) => permission.id === value)?.id ??
          this.unnamedFilter());

    if (next === this.permission()) {
      return;
    }

    this._applyQuery(
      {
        [PERMISSION_PARAM]: next,
        // Same reason a new term resets it: a narrowed directory has fewer pages.
        [PAGE_PARAM]: null,
      },
      // `shouldReplaceHistory`'s rule, applied to a filter rather than a term: turning one
      // on or off is a transition and pushes, swapping one for another is a refinement and
      // replaces. A closed `<select>` commits on every arrow key, so pushing
      // unconditionally would cost a keyboard reader one history entry per permission
      // walked past and as many Backs to escape — where a pointer costs one.
      next !== null && this.permission() !== null,
    );
  }

  protected onPageSizeChanged(size: number): void {
    // The page number cannot survive a resize — row 26 is page 2 at 25 per page and page
    // 1 at 50 — so the reader is returned to the first page rather than to an offset
    // that means something different than it did a moment ago.
    this._applyQuery(
      {
        [SIZE_PARAM]: size === USER_PAGE_SIZES[0] ? null : size,
        [PAGE_PARAM]: null,
      },
      false,
    );
  }

  /**
   * Clears whichever filters are on, from the empty state's own action.
   *
   * One navigation rather than two: clearing them separately would push an intermediate
   * directory the reader never asked for onto the history stack.
   */
  protected clearFilters(): void {
    this._applyQuery(
      { [NAME_PARAM]: null, [PERMISSION_PARAM]: null, [PAGE_PARAM]: null },
      shouldReplaceHistory(this.name(), ''),
    );

    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a table the reader then has to find again. Both targets are outside
    // every branch of the template, so both are always connected — and the one to land on
    // is whichever filter the reader was actually working with.
    if (this.users.name().trim() === '' && this._filteredByPermission()) {
      this._permissionField().nativeElement.focus();
    } else {
      this._search().focusField();
    }
  }

  /** The way out of a page past the end of the directory, which is not a cleared search. */
  protected toFirstPage(): void {
    this.onPageChanged(1);
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
      // A page has to survive a size change, and a search has to survive both.
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
