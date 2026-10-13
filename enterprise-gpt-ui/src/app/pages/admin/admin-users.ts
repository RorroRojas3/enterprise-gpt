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
import { PermissionDto, UserDto } from '@shared/models/api/user.model';
import { TableColumn } from '@shared/models/table-column.model';
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
} from '@shared/utils/navigation/admin-users-route';
import { canRetry } from '@core/errors/error-message';
import { SessionStore } from '@core/session/session-store';
import { SELF_DEACTIVATE_MESSAGE } from '@core/users/self-action-messages';
import { AdminUsersStore } from '@state/admin/admin-users-store';
import { UserActionsStore } from '@state/user-actions-store';
import { AvatarInitials } from '@shared/components/avatar-initials/avatar-initials';
import { DataTable } from '@shared/components/data-table/data-table';
import { EmptyState } from '@shared/components/empty-state/empty-state';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { Paginator } from '@shared/components/paginator/paginator';
import { SearchInput } from '@shared/components/search-input/search-input';
import { TableCell } from '@shared/directives/table-cell';
import { Tooltip } from '@shared/directives/tooltip';
import { PermissionBadge } from '@components/admin/permission-badge';
import { DeactivateUserDialog } from '@components/admin/users/deactivate-user-dialog';
import { UserEditPanel } from '@components/admin/users/user-edit-panel';
import { UserFormDialog } from '@components/admin/users/user-form-dialog';

/**
 * The URL is the query's source of truth and {@link _applyQuery} its only writer. Bind `[value]`
 * one-way and never navigate from an effect over the inputs; the permission-repair effect is the
 * one exception, safe because its navigation clears the condition it reads.
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
  host: { class: 'd-block' },
  templateUrl: './admin-users.html',
  styleUrl: './admin-users.scss',
})
export class AdminUsers {
  /** Trimmed, so a removed key (`undefined`) or a hand-edited `?name=%20%20` means unfiltered. */
  readonly name = input('', {
    transform: (value: string | undefined) => value?.trim() ?? '',
  });

  readonly page = input(1, { transform: toPage });
  readonly size = input(USER_PAGE_SIZES[0] as number, { transform: toPageSize });

  /** Normalized only; {@link permissionFilter} checks the id against the catalog. */
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
   * Not `permissions().length > 0`: a `200 []` would never settle and leave a permanent
   * skeleton. A failure settles it too, since the question is whether the id can be checked.
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
   * An id the catalog does not list reads as no filter, so the select never shows "any" over a
   * narrowed request. If the catalog failed, the id is sent as asked and the server decides.
   */
  protected readonly permissionFilter = computed(() => {
    const id = this.permission();

    if (id === null || this.actions.permissionsError() !== null) {
      return id;
    }

    return this.selectedPermission()?.id ?? null;
  });

  /**
   * The applied filter no option can name (after a failed catalog), given its own option so the
   * select never claims "any" over filtered rows. Keyed on the name: the error clears on retry.
   */
  protected readonly unnamedFilter = computed(() =>
    this.users.permissionId() !== null && this._appliedPermissionName() === null
      ? this.users.permissionId()
      : null,
  );

  /** A permission link waits for the catalog behind a skeleton, not an unfiltered page. */
  protected readonly awaitingPermissions = computed(
    () => this.permission() !== null && !this._catalogSettled(),
  );

  /**
   * Also covers the catalog wait, but only with no rows: a catalog retry clears the error early,
   * and rows already on screen must not give way to a skeleton.
   */
  protected readonly isTableLoading = computed(
    () =>
      this.users.isFirstLoad() ||
      (this.awaitingPermissions() && this.users.entities().length === 0),
  );

  /**
   * Reads the store's filters, which the rendered rows belong to, not the inputs. Names the
   * permission from the catalog, with neutral wording when it cannot.
   */
  protected readonly noMatchHeading = computed(() => {
    const term = this.users.name().trim();

    if (!this._filteredByPermission()) {
      return `No users match “${term}”`;
    }

    // Branch on whether the filter is on, not whether it can be named, or an unnamed filter
    // would fall into the term arm and render `No users match “”`.
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

  /** From the store, as `AdminUsersStore.hasNoMatches` reads it, so copy and condition agree. */
  private readonly _filteredByPermission = computed(() => this.users.permissionId() !== null);

  /** The applied filter's name; `null` when unfiltered or the catalog cannot name it. */
  private readonly _appliedPermissionName = computed(() => {
    const id = this.users.permissionId();

    if (id === null) {
      return null;
    }

    return this.actions.permissions().find((permission) => permission.id === id)?.name ?? null;
  });

  /** Fields, not template literals: exhaustive no-changes checks see a new value each pass. */
  protected readonly columns: readonly TableColumn<UserDto>[] = [
    { key: 'user', header: 'User', width: 'minmax(0, 1.2fr)', mobile: 'title' },
    { key: 'email', header: 'Email', width: 'minmax(0, 1.3fr)', mobile: 'subtitle' },
    { key: 'permissions', header: 'Permissions', width: 'minmax(0, 1.7fr)', mobile: 'badges' },
    // headerHidden, not an empty header: a screen reader listing the columns still needs a name.
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
   * Falls back to the email: `fullName` is empty for a Graph account with no given name or
   * surname, and a blank cell reads as a rendering fault.
   */
  protected displayName(user: UserDto): string {
    return user.fullName.trim() === '' ? user.email : user.fullName;
  }

  /** For the `(you)` marker. `UserDto.id` is the Entra `oid`, which the session's user shares. */
  protected isSelf(user: UserDto): boolean {
    return user.id === this._session.user()?.id;
  }

  /** The id of a row's inline refusal, which its Deactivate control is described by. */
  protected refusalId(user: UserDto): string {
    return `admin-users-refusal-${user.id}`;
  }

  /**
   * Removes the row at once and hands the undo to the root store as a callback, since a root
   * store cannot inject this route-scoped one.
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
    // One computation, so a deep link sends a single request carrying every parameter.
    this.users.bindQuery(() =>
      // `null` withholds the request until the catalog vouches for the permission, rather
      // than flashing the whole directory.
      this.awaitingPermissions()
        ? null
        : {
            name: this.name(),
            page: this.page(),
            pageSize: this.size(),
            permissionId: this.permissionFilter(),
          },
    );

    // Fetched on mount, since the filter renders from it; the root store's cache serves the
    // edit panel and checklist too.
    this.actions.ensurePermissions();

    // Drops a `?permission=` the catalog disowns, replacing the URL the reader did not author;
    // the navigation clears the condition, so it cannot loop. A failed catalog disowns nothing,
    // so that filter stays and shows through `unnamedFilter`.
    effect(() => {
      if (
        this.permission() !== null &&
        this._catalogSettled() &&
        this.permissionFilter() === null
      ) {
        // `untracked`, so the effect does not depend on signals the router reads.
        untracked(() => this._applyQuery({ [PERMISSION_PARAM]: null }, true));
      }
    });

    // The root store outlives this route; without this, returning would re-announce a refusal.
    this.actions.clearSelfDeactivateRefusal();
  }

  protected onSearched(term: string): void {
    const next = term.trim();

    this._applyQuery(
      {
        // `null` removes the key rather than leaving `?name=` behind.
        [NAME_PARAM]: next === '' ? null : next,
        // A new term rarely has as many pages; staying on page 4 would show an empty table.
        [PAGE_PARAM]: null,
      },
      shouldReplaceHistory(this.name(), next),
    );
  }

  protected onPageChanged(page: number): void {
    // Never replaces: a page change is a discrete transition, so Back returns to the last page.
    this._applyQuery({ [PAGE_PARAM]: page === 1 ? null : page }, false);
  }

  protected onPermissionFiltered(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;

    // Checked against the catalog the options render from, not trusted.
    const next =
      value === ''
        ? null
        : // Falls back to the unnamed filter, so re-selecting it no-ops instead of clearing.
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
      // Toggling a filter pushes; swapping one replaces, since a closed `<select>` commits on
      // every arrow key and would otherwise cost a keyboard reader one Back per option.
      next !== null && this.permission() !== null,
    );
  }

  protected onPageSizeChanged(size: number): void {
    // A page number means a different offset at another size, so a resize returns to page 1.
    this._applyQuery(
      {
        [SIZE_PARAM]: size === USER_PAGE_SIZES[0] ? null : size,
        [PAGE_PARAM]: null,
      },
      false,
    );
  }

  /** One navigation, so no intermediate directory lands on the history stack. */
  protected clearFilters(): void {
    this._applyQuery(
      { [NAME_PARAM]: null, [PERMISSION_PARAM]: null, [PAGE_PARAM]: null },
      shouldReplaceHistory(this.name(), ''),
    );

    // The button unmounts when rows return, so focus moves to the filter the reader was using;
    // both sit outside every template branch and are always connected.
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

  /** The single writer: every query change is a navigation, so the URL stays authoritative. */
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
