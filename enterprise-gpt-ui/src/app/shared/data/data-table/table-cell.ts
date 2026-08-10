import { Directive, TemplateRef, inject, input } from '@angular/core';
import { TableCellContext } from './table-column';

/**
 * The template that renders one column's cells.
 *
 * ```html
 * <ng-template appTableCell="name" [appTableCellFor]="store.rows()" let-row>
 *   <a [routerLink]="['/chat', row.id]">{{ row.name }}</a>
 * </ng-template>
 * ```
 *
 * `appTableCellFor` exists only to give `T` an inference site — the same trick
 * `NgFor` uses — so `let-row` is typed as the row and `row.nmae` is a compile error
 * rather than `undefined` on screen.
 */
@Directive({ selector: 'ng-template[appTableCell]' })
export class TableCell<T> {
  /** The `TableColumn.key` this template renders. */
  readonly appTableCell = input.required<string>();

  /** Type anchor. Bind the row array; it is never read at run time. */
  readonly appTableCellFor = input.required<readonly T[]>();

  readonly template = inject<TemplateRef<TableCellContext<T>>>(TemplateRef);

  // A pure type assertion: both parameters exist only to shape the guard's signature,
  // which is why neither is read.
  static ngTemplateContextGuard<T>(
    _directive: TableCell<T>,
    _context: unknown,
  ): _context is TableCellContext<T> {
    return true;
  }
}
