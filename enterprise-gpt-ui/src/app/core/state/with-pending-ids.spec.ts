import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { patchState, signalStore } from '@ngrx/signals';
import { unprotected } from '@ngrx/signals/testing';
import { describe, expect, it } from 'vitest';
import { addPendingId, clearPendingIds, removePendingId, withPendingIds } from './with-pending-ids';

const Store = signalStore({ providedIn: 'root' }, withPendingIds());

const ROWS = ['a', 'b', 'c'];

@Component({
  selector: 'app-pending-rows',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (id of rows; track id) {
      <span data-testid="row">{{ id }}{{ store.isRowPending(id) ? ':busy' : '' }}</span>
    }
  `,
})
class PendingRows {
  readonly store = inject(Store);
  readonly rows = ROWS;
}

describe('withPendingIds', () => {
  it('starts with nothing pending', () => {
    const store = TestBed.inject(Store);

    expect(store.hasPendingIds()).toBe(false);
    expect(store.pendingIdCount()).toBe(0);
    expect(store.isRowPending('a')).toBe(false);
  });

  it('reports only the row whose action is in flight', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), addPendingId('b'));

    expect(store.isRowPending('b')).toBe(true);
    expect(store.isRowPending('a')).toBe(false);
    expect(store.isRowPending('c')).toBe(false);
    expect(store.pendingIdCount()).toBe(1);
  });

  it('tracks several rows at once', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), addPendingId('a'));
    patchState(unprotected(store), addPendingId('c'));

    expect(store.isRowPending('a')).toBe(true);
    expect(store.isRowPending('c')).toBe(true);
    expect(store.isRowPending('b')).toBe(false);
    expect(store.pendingIdCount()).toBe(2);
  });

  it('clears one row without disturbing the others', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), addPendingId('a'));
    patchState(unprotected(store), addPendingId('b'));
    patchState(unprotected(store), removePendingId('a'));

    expect(store.isRowPending('a')).toBe(false);
    expect(store.isRowPending('b')).toBe(true);
  });

  it('is idempotent on a repeated add and an unknown remove', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), addPendingId('a'));
    patchState(unprotected(store), addPendingId('a'));
    patchState(unprotected(store), removePendingId('zzz'));

    expect(store.pendingIdCount()).toBe(1);
    expect(store.isRowPending('a')).toBe(true);
  });

  it('replaces the set rather than mutating it, so change detection sees the write', () => {
    const store = TestBed.inject(Store);
    const before = store.pendingIds();

    patchState(unprotected(store), addPendingId('a'));

    expect(store.pendingIds()).not.toBe(before);
    expect(before.has('a')).toBe(false);
  });

  it('re-renders only the affected row in a zoneless OnPush view', async () => {
    const fixture = TestBed.createComponent(PendingRows);
    const store = TestBed.inject(Store);
    const text = () =>
      Array.from(fixture.nativeElement.querySelectorAll('[data-testid="row"]')).map(
        (el) => (el as HTMLElement).textContent,
      );

    // whenStable rather than detectChanges: the latter drives a tick directly, so
    // it would still pass on a write that never notified the scheduler — which is
    // half of what this test exists to catch under zoneless.
    await fixture.whenStable();
    expect(text()).toEqual(['a', 'b', 'c']);

    patchState(unprotected(store), addPendingId('b'));
    await fixture.whenStable();

    // The signal read inside isRowPending is what the template tracks; a mutated
    // Set would never have reached the signal and this would still read 'b'.
    expect(text()).toEqual(['a', 'b:busy', 'c']);

    patchState(unprotected(store), removePendingId('b'));
    await fixture.whenStable();

    expect(text()).toEqual(['a', 'b', 'c']);
  });

  it('empties on clear', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), addPendingId('a'));
    patchState(unprotected(store), addPendingId('b'));
    patchState(unprotected(store), clearPendingIds());

    expect(store.hasPendingIds()).toBe(false);
    expect(store.isRowPending('a')).toBe(false);
  });
});
