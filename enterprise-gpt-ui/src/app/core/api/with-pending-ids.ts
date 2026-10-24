import { computed } from '@angular/core';
import {
  PartialStateUpdater,
  signalStoreFeature,
  withComputed,
  withMethods,
  withState,
} from '@ngrx/signals';

export interface PendingIdsState {
  /** Ids whose own action is in flight; a `Set` because every row looks itself up per render. */
  readonly pendingIds: ReadonlySet<string>;
}

/**
 * Per-row in-flight tracking. Updaters must replace the `Set`: a mutated one keeps its
 * reference, so no signal write happens and the list silently stops updating.
 */
export function withPendingIds() {
  return signalStoreFeature(
    withState<PendingIdsState>({ pendingIds: new Set<string>() }),
    withComputed(({ pendingIds }) => ({
      hasPendingIds: computed(() => pendingIds().size > 0),
      pendingIdCount: computed(() => pendingIds().size),
    })),
    withMethods(({ pendingIds }) => ({
      /** Whether this row's own action is in flight. */
      isRowPending(id: string): boolean {
        return pendingIds().has(id);
      },
    })),
  );
}

export function addPendingId(id: string): PartialStateUpdater<PendingIdsState> {
  return ({ pendingIds }) => ({ pendingIds: new Set(pendingIds).add(id) });
}

export function removePendingId(id: string): PartialStateUpdater<PendingIdsState> {
  return ({ pendingIds }) => {
    const next = new Set(pendingIds);
    next.delete(id);
    return { pendingIds: next };
  };
}

export function clearPendingIds(): PendingIdsState {
  return { pendingIds: new Set<string>() };
}
