/**
 * Selection updaters for {@link DataTable}. Each returns a new `Set`: writing back a mutated one
 * is no signal write, and the checkbox looks dead.
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
