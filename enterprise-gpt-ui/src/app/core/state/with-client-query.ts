import { Signal, computed, isSignal } from '@angular/core';
import { signalStoreFeature, withComputed, withState } from '@ngrx/signals';

export type SortDirection = 'asc' | 'desc';

export interface ClientQueryState<K extends string> {
  readonly query: string;
  readonly sortKey: K | null;
  readonly sortDirection: SortDirection;
}

export interface ClientQueryConfig<T, K extends string> {
  /**
   * The text one row is matched against. Concatenate whatever fields the screen
   * says it searches; matching is case-insensitive substring.
   */
  readonly searchableText: (item: T) => string;
  /** Ascending comparators, one per offered sort key. */
  readonly comparators: Readonly<Record<K, (a: T, b: T) => number>>;
  /**
   * Whether `source` is the complete matching set.
   *
   * A signal where it depends on load state — a drain that has not finished, or
   * one that stopped at its ceiling, is not authoritative and must not offer sort.
   * Note that `isFullyLoaded` alone reads `true` before the first fetch, so gate it
   * on request status too: `computed(() => isFulfilled() && isFullyLoaded())`.
   */
  readonly isAuthoritative: Signal<boolean> | boolean;
  /**
   * Why the loaded set is incomplete, in the user's words — "Only the first 500
   * projects have loaded." Rendered wherever the shortfall changes what the screen
   * is telling the user: in place of a sort control, and beside a result count that
   * was computed over part of the data. Naming the limit is the point; a silently
   * disabled control reads as a bug, and an unqualified "No matches" is a lie.
   */
  readonly incompleteSetReason: string;
}

/**
 * Client-side search and sort over a collection the store already holds.
 *
 * No list endpoint in this API accepts a sort parameter, so sorting is only ever
 * correct over a set the client holds in full. This feature makes that condition
 * explicit rather than implicit: when `isAuthoritative` is false the sort keys are
 * ignored and results stay in server order. Sorting only the page in hand is never
 * an option — "Load more" would then append a second sorted run beneath the first.
 *
 * Filtering is *not* suppressed on an incomplete set, because a filter over part of
 * the data is still useful where a sort over it is simply wrong. It is qualified
 * instead: `isResultPartial` says the count on screen was computed over less than
 * everything, so "No projects match" can be rendered as "No matches among the
 * projects loaded so far" rather than as a fact about the user's data.
 *
 * Composed through `withFeature` so it binds to any `Signal<readonly T[]>`,
 * whatever the host store calls it:
 *
 * ```ts
 * signalStore(
 *   withEntities<Project>(),
 *   withRequestStatus(),
 *   withOffsetPagination(),
 *   withFeature(({ entities, isFulfilled, isFullyLoaded }) =>
 *     withClientQuery(entities, {
 *       searchableText: (p) => p.name,
 *       comparators: { name: (a, b) => a.name.localeCompare(b.name) },
 *       // Both, not isFullyLoaded alone: it reads true before the first fetch, and
 *       // a sort control that appears over zero rows and then vanishes when the
 *       // first page lands is worse than one that was never offered.
 *       isAuthoritative: computed(() => isFulfilled() && isFullyLoaded()),
 *       incompleteSetReason: 'Only the first 500 projects have loaded.',
 *     }),
 *   ),
 * );
 * ```
 */
export function withClientQuery<T, K extends string>(
  source: Signal<readonly T[]>,
  config: ClientQueryConfig<T, K>,
) {
  const canSort: Signal<boolean> = isSignal(config.isAuthoritative)
    ? config.isAuthoritative
    : computed(() => config.isAuthoritative as boolean);

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

      /**
       * The result set was computed over less than everything that matches, so
       * a count or an empty state built from it must say so.
       */
      const isResultPartial = computed(() => !canSort() && hasQuery());

      return {
        canSort,
        sortUnavailableReason: computed(() => (canSort() ? null : config.incompleteSetReason)),
        /** Rows to render: filtered always, sorted only where sorting is honest. */
        results: computed(() => {
          const items = matches();
          const key = sortKey();

          if (!canSort() || key === null) {
            return items;
          }

          const compare = config.comparators[key];
          const sign = sortDirection() === 'desc' ? -1 : 1;
          // Copied before sorting: `sort` is in place, and the source array is
          // store state.
          return [...items].sort((a, b) => sign * compare(a, b));
        }),
        hasQuery,
        isEmptyResult: computed(() => matches().length === 0),
        isResultPartial,
        resultsPartialReason: computed(() =>
          isResultPartial() ? config.incompleteSetReason : null,
        ),
      };
    }),
  );
}

/**
 * The part of {@link ClientQueryState} an updater reads, widened to `string`.
 *
 * Updaters are typed against this rather than against `ClientQueryState<K>`: a
 * host store's `sortKey` is its own key union, and an updater declaring the single
 * literal it was called with would not accept that state as its argument.
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
