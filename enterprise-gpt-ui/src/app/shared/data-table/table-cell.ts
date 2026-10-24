import { Directive, TemplateRef, inject, input } from '@angular/core';
import { TableCellContext } from './table-column.model';

/**
 * One column's cell template. `appTableCellFor` exists only to give `T` an inference site, so
 * `let-row` is typed as the row.
 */
@Directive({ selector: 'ng-template[appTableCell]' })
export class TableCell<T> {
  /** The `TableColumn.key` this template renders. */
  readonly appTableCell = input.required<string>();

  /** Type anchor. Bind the row array; it is never read at run time. */
  readonly appTableCellFor = input.required<readonly T[]>();

  readonly template = inject<TemplateRef<TableCellContext<T>>>(TemplateRef);

  // Both parameters exist only to shape the guard's signature.
  static ngTemplateContextGuard<T>(
    _directive: TableCell<T>,
    _context: unknown,
  ): _context is TableCellContext<T> {
    return true;
  }
}
