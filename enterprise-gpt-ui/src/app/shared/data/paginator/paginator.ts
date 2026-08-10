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

  readonly pageChanged = output<number>();

  protected readonly gap = PAGE_GAP;
  protected readonly entries = computed(() => pageWindow(this.page(), this.totalPages()));
  protected readonly isFirst = computed(() => this.page() <= 1);
  protected readonly isLast = computed(() => this.page() >= this.totalPages());

  protected readonly summary = computed(() => {
    const total = this.totalCount();
    if (total === 0) {
      return `No ${this.itemLabel()}`;
    }
    const first = (this.page() - 1) * this.pageSize() + 1;
    const last = Math.min(this.page() * this.pageSize(), total);
    return `Showing ${first}–${last} of ${total} ${this.itemLabel()}`;
  });

  protected go(page: number): void {
    if (page !== this.page() && page >= 1 && page <= this.totalPages()) {
      this.pageChanged.emit(page);
    }
  }
}
