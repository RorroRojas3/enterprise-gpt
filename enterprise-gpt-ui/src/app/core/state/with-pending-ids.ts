import { computed } from '@angular/core';
import {
  PartialStateUpdater,
  signalStoreFeature,
  withComputed,
  withMethods,
  withState,
} from '@ngrx/signals';

export interface PendingIdsState {
  /**
   * Ids whose own action is in flight. A `Set` rather than an array because the
   * lookup happens once per row per render.
   */
  readonly pendingIds: ReadonlySet<string>;
}

/**
 * Per-row in-flight tracking, so a rename on one conversation does not disable
 * the other forty.
 *
 * Every updater replaces the set rather than mutating it, and that is load-bearing
 * rather than stylistic: `patchState` compares each key by reference before
 * writing, so an updater that mutates the existing `Set` and returns the same
 * reference performs **no signal write at all**. Nothing re-renders and nothing
 * fails — the list simply stops updating.
 */
export function withPendingIds() {
  return signalStoreFeature(
    withState<PendingIdsState>({ pendingIds: new Set<string>() }),
    withComputed(({ pendingIds }) => ({
      hasPendingIds: computed(() => pendingIds().size > 0),
      pendingIdCount: computed(() => pendingIds().size),
    })),
    withMethods(({ pendingIds }) => ({
      /**
       * Whether this row's own action is in flight.
       *
       * Reads the signal on every call, so a template binding tracks it and
       * re-renders when the set changes.
       */
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
