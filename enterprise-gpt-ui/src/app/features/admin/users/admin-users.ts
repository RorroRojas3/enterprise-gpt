import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { UserDto } from '@domain/api/user';
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
  NAME_PARAM,
  ORDER_EXPLANATION,
  PAGE_PARAM,
  SIZE_PARAM,
  USER_PAGE_SIZES,
  shouldReplaceHistory,
  toPage,
  toPageSize,
} from './admin-users-route';

/**
 * The administration user directory, frame `5a` (US-1201).
 *
 * **The URL is the source of truth for the query** — `?name=`, `?page=` and `?size=`.
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
 * Three controls frame `5a` draws are deliberately absent. There is **no sort control**,
 * because the endpoint accepts no sort parameter (regime D, and US-705's subject on the
 * conversations library). There is **no row-selection column**, because no endpoint acts
 * on more than one user and a checkbox that can only ever select is not a control. And
 * there is **no "Permission: any" filter**, which is US-1206 and waits on the US-1205
 * enabler that would let it filter more than the loaded page.
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

  protected readonly users = inject(AdminUsersStore);
  protected readonly actions = inject(UserActionsStore);

  private readonly _session = inject(SessionStore);
  private readonly _router = inject(Router);
  private readonly _route = inject(ActivatedRoute);
  private readonly _search = viewChild.required(SearchInput);

  /** A 403 answers the same however often it is asked; a 502 does not. */
  protected readonly canRetry = canRetry;

  protected readonly ORDER_EXPLANATION = ORDER_EXPLANATION;
  protected readonly SELF_DEACTIVATE_MESSAGE = SELF_DEACTIVATE_MESSAGE;
  protected readonly pageSizes = USER_PAGE_SIZES;

  /**
   * Frame `5b`'s empty-state heading, naming the term.
   *
   * Read from the **store**, not from {@link name}: the store's copy is the one the
   * rendered rows belong to, so the heading cannot name a term whose results have not
   * arrived yet.
   */
  protected readonly noMatchHeading = computed(() => `No users match “${this.users.name()}”`);

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
    this.users.bindQuery(() => ({
      name: this.name(),
      page: this.page(),
      pageSize: this.size(),
    }));

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

  protected clearSearch(): void {
    this.onSearched('');
    // The button removes itself the moment rows come back, and focus would fall to
    // `<body>` inside a table the reader then has to find again. The field is outside
    // every branch of the template, so it is always connected.
    this._search().focusField();
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
