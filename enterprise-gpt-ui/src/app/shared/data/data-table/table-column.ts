/** Where a column goes when the table collapses into cards below the breakpoint. */
export type MobileSlot = 'title' | 'subtitle' | 'meta' | 'badges' | 'actions' | 'hidden';

export interface TableColumn<T> {
  /** Matches the `appTableCell` template that renders this column. */
  readonly key: string;
  readonly header: string;
  /** A CSS grid track: `'40px'`, `'1.2fr'`, `'minmax(0, 1fr)'`. */
  readonly width: string;
  readonly align?: 'start' | 'center' | 'end';
  /** For an icon-action column: the header text stays for assistive technology. */
  readonly headerHidden?: boolean;
  /** Used when no cell template matches the key. */
  readonly text?: (row: T) => string;
  readonly mobile?: MobileSlot;
}

/**
 * What a cell template receives. `$implicit` makes `let-row` work; `row` is the same
 * value under a name that reads better at a call site with several `let-` bindings.
 */
export interface TableCellContext<T> {
  readonly $implicit: T;
  readonly row: T;
  readonly rowIndex: number;
}
