import { Signal, computed } from '@angular/core';
import { TableColumn } from '@shared/models/table-column.model';
import { MOBILE_VIEWPORT } from '@shared/utils/layout/breakpoints';
import { injectMediaQuery } from '@core/ui/media-query';

/** Bootstrap's `xxl` edge, local to the admin tables. */
export const BELOW_XXL = '(max-width: 1399.98px)';

/**
 * Drops the `hideOnTablet` columns up to `xxl`, not just on tablets: `DataTable` keys that band to
 * the viewport, and from 992px the persistent sidebar beside the rail leaves the table less room
 * than at 991px. Cards below 768px keep every column.
 */
export function injectAdminColumns<T>(
  columns: readonly TableColumn<T>[],
): Signal<readonly TableColumn<T>[]> {
  const belowXxl = injectMediaQuery(BELOW_XXL);
  const isNarrow = injectMediaQuery(MOBILE_VIEWPORT);
  const compact = columns.filter((column) => !column.hideOnTablet);

  return computed(() => (belowXxl() && !isNarrow() ? compact : columns));
}
