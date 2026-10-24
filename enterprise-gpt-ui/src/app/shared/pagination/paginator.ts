import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Icon } from '@shared/icons/icon';
import { PAGE_GAP, pageWindow } from './page-window';

/** Numbered pager for server-paged screens; "Load more" is a plain button in the table footer. */
@Component({
  selector: 'app-paginator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  host: { class: 'd-flex flex-wrap justify-content-between align-items-center gap-3' },
  templateUrl: './paginator.html',
  styleUrl: './paginator.scss',
})
export class Paginator {
  /** 1-based, matching `PaginatedResponseDto.currentPage`. */
  readonly page = input.required<number>();
  readonly totalPages = input.required<number>();
  readonly totalCount = input.required<number>();
  readonly pageSize = input.required<number>();

  /** Pluralised by the caller: "users", "documents". */
  readonly itemLabel = input.required<string>();

  /** Sizes the "N / page" select offers; empty, the default, renders no select. */
  readonly pageSizes = input<readonly number[]>([]);

  /** False when the same sentence goes to `DataTable.summary`, or it is announced twice. */
  readonly liveSummary = input<boolean>(true);

  readonly pageChanged = output<number>();
  readonly pageSizeChanged = output<number>();

  protected readonly gap = PAGE_GAP;
  protected readonly entries = computed(() => pageWindow(this.page(), this.totalPages()));
  // Past the end there is no "previous" the guard in `go` will accept, so the control is
  // genuinely disabled rather than enabled and inert.
  protected readonly isFirst = computed(() => this.page() <= 1 || this.page() > this.totalPages());
  protected readonly isLast = computed(() => this.page() >= this.totalPages());

  protected readonly summary = computed(() => {
    const total = this.totalCount();
    if (total === 0) {
      return `No ${this.itemLabel()}`;
    }
    const first = (this.page() - 1) * this.pageSize() + 1;
    // A page past the end — a stale deep link, or one emptied since it was shared — has
    // no range to state. Unclamped this reads "Showing 2451–312 of 312 users".
    if (first > total) {
      return `No ${this.itemLabel()} on this page — ${total} in total`;
    }
    const last = Math.min(this.page() * this.pageSize(), total);
    return `Showing ${first}–${last} of ${total} ${this.itemLabel()}`;
  });

  protected go(page: number): void {
    if (page !== this.page() && page >= 1 && page <= this.totalPages()) {
      this.pageChanged.emit(page);
    }
  }

  protected onPageSize(event: Event): void {
    const next = Number((event.target as HTMLSelectElement).value);

    // Guarded rather than trusted: the option list is the caller's, and a size that is
    // not on it — or is not a number at all — must not reach a request as `take=NaN`.
    if (Number.isFinite(next) && next !== this.pageSize() && this.pageSizes().includes(next)) {
      this.pageSizeChanged.emit(next);
    }
  }
}
