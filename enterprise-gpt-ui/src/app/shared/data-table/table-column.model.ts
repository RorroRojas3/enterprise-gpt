/**
 * A column's place on the mobile card. `trailing` sits beside the title, for one icon control;
 * `actions` is the full-width foot for labelled buttons, where a lone kebab looks broken.
 */
export type MobileSlot =
  'title' | 'subtitle' | 'meta' | 'badges' | 'trailing' | 'actions' | 'hidden';

export interface TableColumn<T> {
  /** Matches the `appTableCell` template that renders this column. */
  readonly key: string;
  /** Also the visible label this column's value carries in the card's `meta` slot. */
  readonly header: string;
  /** A CSS grid track: `'40px'`, `'1.2fr'`, `'minmax(0, 1fr)'`. */
  readonly width: string;
  readonly align?: 'start' | 'center' | 'end';
  /** For an icon-action column: the header text stays for assistive technology. */
  readonly headerHidden?: boolean;
  /** Used when no cell template matches the key. */
  readonly text?: (row: T) => string;
  readonly mobile?: MobileSlot;
  /**
   * Dropped from the table between 768 and 991px, where the remaining tracks would
   * otherwise ellipsise to nothing. The card branch below 768px still renders it.
   */
  readonly hideOnTablet?: boolean;
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
