import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { Dispatcher, event } from '@ngrx/signals/events';
import { unprotected } from '@ngrx/signals/testing';
import { delay, of, takeUntil } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { injectSignedOut, sessionEvents } from '../events/session-events';
import { toAppError } from '../errors/to-app-error';
import { setFirstPage, setPage, withOffsetPagination } from './with-offset-pagination';
import { addPendingId, withPendingIds } from './with-pending-ids';
import { setError, setPending, withRequestStatus } from './with-request-status';
import { withResetOnSignOut } from './with-reset-on-sign-out';

interface Conversation {
  readonly id: string;
  readonly name: string;
}

const CONVERSATIONS: Conversation[] = [
  { id: 'a', name: 'First' },
  { id: 'b', name: 'Second' },
];

const Store = signalStore(
  { providedIn: 'root' },
  withState({ selectedId: null as string | null }),
  withEntities<Conversation>(),
  withRequestStatus(),
  withOffsetPagination(50),
  withPendingIds(),
  // Last, so it snapshots everything above.
  withResetOnSignOut(),
);

const failure = toAppError(
  new HttpErrorResponse({
    error: PROBLEM_FIXTURES.forbidden,
    status: 403,
    url: 'https://localhost:7045/api/conversations',
  }),
);

function signOut(): void {
  TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
}

/** Lets a `delay(0)` emission land, or prove that it did not. */
function settle(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 1));
}

describe('withResetOnSignOut', () => {
  it('clears state contributed by every composed feature', () => {
    const store = TestBed.inject(Store);

    patchState(
      unprotected(store),
      { selectedId: 'a' },
      setAllEntities(CONVERSATIONS),
      setPage({
        items: CONVERSATIONS,
        totalCount: 12,
        pageSize: 50,
        currentPage: 1,
        totalPages: 1,
      }),
      setPending(),
      addPendingId('a'),
    );

    expect(store.entities()).toHaveLength(2);

    signOut();

    expect(store.selectedId()).toBeNull();
    expect(store.entities()).toEqual([]);
    expect(store.ids()).toEqual([]);
    expect(store.requestStatus()).toBe('idle');
    expect(store.skip()).toBe(0);
    expect(store.totalCount()).toBe(0);
    expect(store.pendingIds().size).toBe(0);
  });

  it('restores the configured page size after the server changed it', () => {
    const store = TestBed.inject(Store);

    patchState(
      unprotected(store),
      setFirstPage({ items: [], totalCount: 0, pageSize: 25, currentPage: 1, totalPages: 0 }),
    );
    expect(store.take()).toBe(25);

    signOut();

    expect(store.take()).toBe(50);
  });

  it('clears an error left by a failed load', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), setError(failure));
    expect(store.error()).not.toBeNull();

    signOut();

    expect(store.error()).toBeNull();
  });

  it('resets again on a second sign-out', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), { selectedId: 'a' });
    signOut();
    patchState(unprotected(store), { selectedId: 'b' });
    signOut();

    expect(store.selectedId()).toBeNull();
  });

  it('resets a store whose initial state is not empty back to that state', () => {
    const Themed = signalStore(
      { providedIn: 'root' },
      withState({ theme: 'dark', pinned: ['home'] as readonly string[] }),
      withResetOnSignOut(),
    );
    const store = TestBed.inject(Themed);

    patchState(unprotected(store), { theme: 'light', pinned: ['home', 'reports'] });
    signOut();

    expect(store.theme()).toBe('dark');
    expect(store.pinned()).toEqual(['home']);
  });

  it('resets every composing store from one dispatch', () => {
    const Other = signalStore(
      { providedIn: 'root' },
      withState({ count: 0 }),
      withMethods((store) => ({
        bump: () => patchState(store, ({ count }) => ({ count: count + 1 })),
      })),
      withResetOnSignOut(),
    );
    const conversations = TestBed.inject(Store);
    const other = TestBed.inject(Other);

    patchState(unprotected(conversations), { selectedId: 'a' });
    other.bump();

    signOut();

    expect(conversations.selectedId()).toBeNull();
    expect(other.count()).toBe(0);
  });

  it('does not reset on an unrelated event', () => {
    const store = TestBed.inject(Store);

    const unrelated = event('[Something Else] happened');

    patchState(unprotected(store), { selectedId: 'a' });
    TestBed.inject(Dispatcher).dispatch(unrelated());

    expect(store.selectedId()).toBe('a');
  });

  it('stops listening once a route-scoped store is destroyed', () => {
    const Scoped = signalStore(
      withState({ selectedId: null as string | null }),
      withResetOnSignOut(),
    );

    @Component({
      selector: 'app-scoped-host',
      template: '',
      changeDetection: ChangeDetectionStrategy.OnPush,
      providers: [Scoped],
    })
    class ScopedHost {
      readonly store = inject(Scoped);
    }

    const fixture = TestBed.createComponent(ScopedHost);
    const store = fixture.componentInstance.store;

    patchState(unprotected(store), { selectedId: 'a' });
    fixture.destroy();

    // The store outlives its injector here only because the spec holds a
    // reference; the handler must already have been torn down, so the state it
    // would have written is untouched and nothing throws.
    expect(() => signOut()).not.toThrow();
    expect(store.selectedId()).toBe('a');
  });

  it('delivers a response that lands while the session is live', async () => {
    const signedOut$ = TestBed.runInInjectionContext(() => injectSignedOut());
    const received: string[] = [];

    of('rows')
      .pipe(delay(0), takeUntil(signedOut$))
      .subscribe((rows) => received.push(rows));
    await settle();

    expect(received).toEqual(['rows']);
  });

  it('drops a response that was already on the wire when sign-out fired', async () => {
    const signedOut$ = TestBed.runInInjectionContext(() => injectSignedOut());
    const received: string[] = [];

    // The failure this prevents: the response resolves after the reset and writes
    // the previous user's rows back over the cleared store.
    of('rows')
      .pipe(delay(0), takeUntil(signedOut$))
      .subscribe((rows) => received.push(rows));
    signOut();
    await settle();

    expect(received).toEqual([]);
  });

  it('throws at startup when composed before a state-contributing feature', () => {
    const Misordered = signalStore(
      { providedIn: 'root' },
      withState({ a: 1 }),
      withResetOnSignOut(),
      // Composed after the reset, so its state would survive sign-out.
      withState({ b: 2 }),
    );

    expect(() => TestBed.inject(Misordered)).toThrowError(/must be the last feature/);
  });
});
