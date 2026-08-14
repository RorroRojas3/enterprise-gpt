import {
  ChangeDetectionStrategy,
  Component,
  computed,
  contentChildren,
  input,
  model,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { CardRow } from '@shared/data/card-row/card-row';
import { injectMediaQuery } from '@shared/layout/media-query';
import { toggleRow } from '../row-selection';
import { TableCell } from './table-cell';
import { MobileSlot, TableCellContext, TableColumn } from './table-column';

let nextId = 0;

/** A row's template context, plus the values the table would otherwise recompute. */
interface RowView<T> extends TableCellContext<T> {
  readonly key: string;
  readonly selectLabel: string;
}

/**
 * The list surface every library and admin screen composes.
 *
 * It imports no store. Everything comes in as inputs, which is what lets it serve
 * `withOffsetPagination` (the footer slot), `withPendingIds` (`pendingIds`) and
 * `withClientQuery` (the notice slot) without knowing any of them exist.
 *
 * Below `mobileBreakpoint` it renders {@link CardRow}s instead of a table, because the
 * boards restructure the row at that width rather than reflowing it.
 */
@Component({
  selector: 'app-data-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, Skeleton, CardRow],
  templateUrl: './data-table.html',
  styleUrl: './data-table.scss',
})
export class DataTable<T> {
  readonly rows = input.required<readonly T[]>();
  readonly columns = input.required<readonly TableColumn<T>[]>();

  /** The stable identity of a row — used for tracking and for selection. */
  readonly trackKey = input.required<(row: T) => string>();

  /** How a row is named to a screen reader in the select checkbox. */
  readonly rowLabel = input.required<(row: T) => string>();

  /** The table's own accessible name: "Conversations", "Users". */
  readonly label = input.required<string>();

  readonly loading = input<boolean>(false);
  readonly skeletonRows = input<number>(8);

  /**
   * Overrides the row count announced to assistive technology and used as the table's
   * description.
   *
   * A paginated caller knows something this component cannot: how many rows exist
   * beyond the ones it was handed. Left empty, the default "N results" stands; set,
   * it replaces it entirely, so a screen showing a visible "Showing 25 of 312" is not
   * also announcing "25 results" from here.
   */
  readonly summary = input<string>('');

  /** From `withPendingIds`: these rows are busy; every other row stays interactive. */
  readonly pendingIds = input<ReadonlySet<string>>(new Set<string>());

  readonly selectable = input<boolean>(false);
  readonly selectedIds = model<ReadonlySet<string>>(new Set<string>());

  /**
   * Whether the header row carries the select-all checkbox.
   *
   * Off for a caller whose board draws select-all in its own toolbar: two controls
   * bound to one state is a defect, and below the breakpoint there is no header row
   * to hold this one at all.
   */
  readonly headerSelectAll = input<boolean>(true);

  protected readonly summaryId = `data-table-summary-${nextId++}`;

  // descendants: true, because a consumer's cell templates are frequently wrapped —
  // in an @if for a permission-gated column, or an <ng-container>. Without it the
  // template is simply not found and the column silently renders blank.
  protected readonly cells = contentChildren(TableCell<T>, { descendants: true });

  /**
   * The breakpoint is fixed at 768px rather than an input: `injectMediaQuery` reads
   * its argument once at construction, so a per-instance value could not be honoured
   * without rebuilding the listener, and no screen in the design wants a different one.
   */
  protected readonly isNarrow = injectMediaQuery('(max-width: 767px)');

  protected readonly cellTemplates = computed(
    () => new Map(this.cells().map((cell) => [cell.appTableCell(), cell.template])),
  );

  /**
   * Row contexts, memoised.
   *
   * Binding `[ngTemplateOutletContext]` to a method call would allocate a new object
   * on every check, and `provideCheckNoChangesConfig({ exhaustive: true })` fails that
   * immediately — correctly, because in a zoneless app it also means the view is
   * dirtied on every pass.
   */
  protected readonly rowViews = computed<readonly RowView<T>[]>(() => {
    const key = this.trackKey();
    const label = this.rowLabel();
    return this.rows().map((row, rowIndex) => ({
      $implicit: row,
      row,
      rowIndex,
      // Precomputed rather than called per binding: `rowLabel` is consumer-supplied
      // and would otherwise run once per row on every check, doubled by the
      // exhaustive check-no-changes pass.
      key: key(row),
      selectLabel: `Select ${label(row)}`,
    }));
  });

  /**
   * Columns bucketed by mobile slot, once per column set.
   *
   * Filtering inside the template would allocate five fresh arrays per card row per
   * change-detection pass — the same class of per-check allocation `rowViews` exists
   * to remove.
   */
  protected readonly columnsBySlot = computed<
    Readonly<Record<MobileSlot, readonly TableColumn<T>[]>>
  >(() => {
    const buckets: Record<MobileSlot, TableColumn<T>[]> = {
      title: [],
      subtitle: [],
      meta: [],
      badges: [],
      actions: [],
      hidden: [],
    };
    for (const column of this.columns()) {
      buckets[column.mobile ?? 'hidden'].push(column);
    }
    return buckets;
  });

  protected readonly gridTemplate = computed(() =>
    (this.selectable() ? ['40px'] : [])
      .concat(this.columns().map((column) => column.width))
      .join(' '),
  );

  protected readonly skeletonPlaceholders = computed(() =>
    Array.from({ length: Math.max(1, this.skeletonRows()) }),
  );

  protected readonly allSelected = computed(() => {
    const views = this.rowViews();
    return views.length > 0 && views.every((view) => this.selectedIds().has(view.key));
  });

  protected readonly someSelected = computed(
    () => this.selectedIds().size > 0 && !this.allSelected(),
  );

  protected readonly resultSummary = computed(() => {
    const supplied = this.summary();
    if (supplied !== '') {
      return supplied;
    }

    const count = this.rows().length;
    return count === 1 ? '1 result' : `${count} results`;
  });

  protected isPending(view: RowView<T>): boolean {
    return this.pendingIds().has(view.key);
  }

  protected isSelected(view: RowView<T>): boolean {
    return this.selectedIds().has(view.key);
  }

  protected cellText(column: TableColumn<T>, row: T): string {
    return column.text?.(row) ?? '';
  }

  protected onToggleRow(view: RowView<T>): void {
    this.selectedIds.update((ids) => toggleRow(ids, view.key));
  }

  /** Select-all covers the loaded rows only; the caller states that in the notice. */
  protected onToggleAll(): void {
    const keys = this.rowViews().map((view) => view.key);
    this.selectedIds.set(this.allSelected() ? new Set() : new Set(keys));
  }
}
