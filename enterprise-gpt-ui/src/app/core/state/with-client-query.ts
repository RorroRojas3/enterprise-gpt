import { Signal, computed, isSignal } from '@angular/core';
import { signalStoreFeature, withComputed, withState } from '@ngrx/signals';

export type SortDirection = 'asc' | 'desc';

export interface ClientQueryState<K extends string> {
  readonly query: string;
  readonly sortKey: K | null;
  readonly sortDirection: SortDirection;
}

export interface ClientQueryConfig<T, K extends string> {
  /** Text a row is matched against, as a case-insensitive substring. */
  readonly searchableText: (item: T) => string;
  /** Ascending comparators, one per offered sort key. */
  readonly comparators: Readonly<Record<K, (a: T, b: T) => number>>;
  /**
   * Whether `source` is the complete set, which sorting requires. `isFullyLoaded` reads true
   * before the first fetch, so gate it: `computed(() => isFulfilled() && isFullyLoaded())`.
   */
  readonly isAuthoritative: Signal<boolean> | boolean;
  /**
   * Why the loaded set is incomplete, in the user's words. Shown in place of the sort control
   * and beside partial counts, since a silently disabled control reads as a bug.
   */
  readonly incompleteSetReason: string | Signal<string>;
}

/**
 * Client-side search and sort, composed via `withFeature`, for endpoints that accept no order.
 * Sorting waits for `isAuthoritative`, since a sorted page breaks on "Load more". Filtering
 * still runs on a partial set, and `isResultPartial` qualifies its count.
 */
export function withClientQuery<T, K extends string>(
  source: Signal<readonly T[]>,
  config: ClientQueryConfig<T, K>,
) {
  const canSort: Signal<boolean> = isSignal(config.isAuthoritative)
    ? config.isAuthoritative
    : computed(() => config.isAuthoritative as boolean);

  const incompleteSetReason: Signal<string> = isSignal(config.incompleteSetReason)
    ? config.incompleteSetReason
    : computed(() => config.incompleteSetReason as string);

  return signalStoreFeature(
    withState<ClientQueryState<K>>({ query: '', sortKey: null, sortDirection: 'asc' }),
    withComputed(({ query, sortKey, sortDirection }) => {
      const hasQuery = computed(() => query().trim() !== '');

      const matches = computed(() => {
        const needle = query().trim().toLocaleLowerCase();
        const items = source();
        return needle === ''
          ? items
          : items.filter((item) =>
              config.searchableText(item).toLocaleLowerCase().includes(needle),
            );
      });

      /** Results cover only part of the data, so counts and empty states must say so. */
      const isResultPartial = computed(() => !canSort() && hasQuery());

      return {
        canSort,
        sortUnavailableReason: computed(() => (canSort() ? null : incompleteSetReason())),
        /** Rows to render: filtered always, sorted only where sorting is honest. */
        results: computed(() => {
          const items = matches();
          const key = sortKey();

          if (!canSort() || key === null) {
            return items;
          }

          const compare = config.comparators[key];
          const sign = sortDirection() === 'desc' ? -1 : 1;
          return [...items].sort((a, b) => sign * compare(a, b));
        }),
        hasQuery,
        isEmptyResult: computed(() => matches().length === 0),
        isResultPartial,
        resultsPartialReason: computed(() => (isResultPartial() ? incompleteSetReason() : null)),
      };
    }),
  );
}

/**
 * {@link ClientQueryState} widened to `string`, so an updater typed on one literal key still
 * accepts a host store's whole key union.
 */
export interface SortReadableState {
  readonly sortKey: string | null;
  readonly sortDirection: SortDirection;
}

export function setQuery(query: string): { query: string } {
  return { query };
}

export function setSort<K extends string>(
  sortKey: K,
  sortDirection: SortDirection = 'asc',
): { sortKey: K; sortDirection: SortDirection } {
  return { sortKey, sortDirection };
}

/** Selects `sortKey`, or flips direction when it is already the active key. */
export function toggleSort<K extends string>(
  sortKey: K,
): (
  state: SortReadableState,
) => { sortKey: K; sortDirection: SortDirection } | { sortDirection: SortDirection } {
  // The flip branch returns no `sortKey` at all: re-emitting the one already in
  // state would widen it back to `string` and stop matching the host's key union.
  return (state) =>
    state.sortKey === sortKey
      ? { sortDirection: state.sortDirection === 'asc' ? 'desc' : 'asc' }
      : { sortKey, sortDirection: 'asc' };
}

export function clearSort(): { sortKey: null; sortDirection: SortDirection } {
  return { sortKey: null, sortDirection: 'asc' };
}
