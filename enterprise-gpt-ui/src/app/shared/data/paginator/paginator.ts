import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Icon } from '@shared/icon/icon';
import { PAGE_GAP, pageWindow } from './page-window';

/**
 * The numbered pager of frame `5a`, for the server-paged screens.
 *
 * Every page is a real `<button>` and the ends are genuinely `disabled` — the board
 * draws them as dimmed `<span>`s, which are neither focusable nor announced as
 * unavailable. That is one of the accessibility gaps the PRD says to correct.
 *
 * The "Load more" affordance of frame `4a` is not this component: a caller drops a
 * plain button into the table's footer slot for that.
 */
@Component({
  selector: 'app-paginator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
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

  /**
   * The sizes the "N / page" select offers, or empty for no select at all.
   *
   * Frame `5a` draws 25/50/100; frame `4a` draws no select, which is why the default
   * is empty rather than a set of numbers a caller has to opt out of.
   */
  readonly pageSizes = input<readonly number[]>([]);

  /**
   * Whether the visible summary announces itself when it changes.
   *
   * A caller whose table already hands the same sentence to `DataTable.summary` sets
   * this false: that region is live too, and two live regions carrying one string make
   * a screen reader read the count twice on every page.
   */
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
