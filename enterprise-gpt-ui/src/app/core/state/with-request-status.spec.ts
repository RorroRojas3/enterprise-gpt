import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { patchState, signalStore, withState } from '@ngrx/signals';
import { unprotected } from '@ngrx/signals/testing';
import { describe, expect, it } from 'vitest';
import { PROBLEM_FIXTURES, TRACE_ID } from '@testing/problem-fixtures';
import { toAppError } from '../errors/to-app-error';
import {
  setError,
  setFulfilled,
  setIdle,
  setPending,
  withRequestStatus,
} from './with-request-status';

const Store = signalStore({ providedIn: 'root' }, withRequestStatus());

const notFound = toAppError(
  new HttpErrorResponse({
    error: PROBLEM_FIXTURES.resourceNotFound,
    status: 404,
    url: 'https://localhost:7045/api/conversations/1',
  }),
);

describe('withRequestStatus', () => {
  it('starts idle', () => {
    const store = TestBed.inject(Store);

    expect(store.requestStatus()).toBe('idle');
    expect(store.isIdle()).toBe(true);
    expect(store.isPending()).toBe(false);
    expect(store.isFulfilled()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('reports pending after setPending', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), setPending());

    expect(store.isPending()).toBe(true);
    expect(store.isFulfilled()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('reports fulfilled after setFulfilled', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), setFulfilled());

    expect(store.isFulfilled()).toBe(true);
    expect(store.isPending()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('exposes the whole AppError, traceId included, after setError', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), setError(notFound));

    expect(store.error()).toBe(notFound);
    expect(store.error()?.traceId).toBe(TRACE_ID);
    expect(store.error()?.kind).toBe('resource-not-found');
  });

  it('returns to idle after setIdle', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), setError(notFound));
    patchState(unprotected(store), setIdle());

    expect(store.isIdle()).toBe(true);
    expect(store.error()).toBeNull();
  });

  it('cannot be pending and errored at once, because the status is one value', () => {
    const store = TestBed.inject(Store);

    patchState(unprotected(store), setPending());
    patchState(unprotected(store), setError(notFound));

    expect(store.isPending()).toBe(false);
    expect(store.error()).toBe(notFound);
  });

  it('composes with a data update in one atomic patch', () => {
    const Composed = signalStore(
      { providedIn: 'root' },
      withState({ names: [] as readonly string[] }),
      withRequestStatus(),
    );
    const store = TestBed.inject(Composed);

    patchState(unprotected(store), { names: ['Ada'] }, setFulfilled());

    expect(store.names()).toEqual(['Ada']);
    expect(store.isFulfilled()).toBe(true);
  });
});
