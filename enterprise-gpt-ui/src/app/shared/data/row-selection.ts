/**
 * Selection updaters for {@link DataTable}.
 *
 * Every one returns a **new** `Set`. `set.add(id)` followed by writing the same
 * reference back performs no signal write at all: the view never re-renders and the
 * bug looks like a dead checkbox. `core/state/with-pending-ids.ts` documents the same
 * rule for the same reason, and the specs here assert `not.toBe` to keep it true.
 */

export function toggleRow(ids: ReadonlySet<string>, id: string): ReadonlySet<string> {
  const next = new Set(ids);
  if (!next.delete(id)) {
    next.add(id);
  }
  return next;
}

export function selectRows(ids: ReadonlySet<string>, add: readonly string[]): ReadonlySet<string> {
  const next = new Set(ids);
  for (const id of add) {
    next.add(id);
  }
  return next;
}
