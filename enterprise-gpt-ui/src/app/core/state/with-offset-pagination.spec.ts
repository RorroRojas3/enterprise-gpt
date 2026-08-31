import { TestBed } from '@angular/core/testing';
import { patchState, signalStore } from '@ngrx/signals';
import { unprotected } from '@ngrx/signals/testing';
import { describe, expect, it } from 'vitest';
import { PaginatedResponseDto } from '@domain/api/paginated-response';
import {
  MAX_PAGE_SIZE,
  setFirstPage,
  setPage,
  withOffsetPagination,
} from './with-offset-pagination';

function page(
  itemCount: number,
  totalCount: number,
  pageSize = 100,
  currentPage = 1,
): PaginatedResponseDto<string> {
  return {
    items: Array.from({ length: itemCount }, (_, i) => `item-${currentPage}-${i}`),
    totalCount,
    pageSize,
    currentPage,
    totalPages: pageSize > 0 ? Math.ceil(totalCount / pageSize) : 0,
  };
}

function storeWith(take?: number) {
  const Store = signalStore({ providedIn: 'root' }, withOffsetPagination(take));
  return TestBed.inject(Store);
}

describe('withOffsetPagination', () => {
  it('starts at offset zero with the default page size', () => {
    const store = storeWith();

    expect(store.skip()).toBe(0);
    expect(store.take()).toBe(MAX_PAGE_SIZE);
    expect(store.totalCount()).toBe(0);
  });

  it('clamps a requested page size above the server maximum', () => {
    expect(storeWith(500).take()).toBe(MAX_PAGE_SIZE);
  });

  it('clamps a requested page size below one', () => {
    expect(storeWith(0).take()).toBe(1);
  });

  it('falls back to the maximum rather than propagating a non-finite page size', () => {
    expect(storeWith(Number.NaN).take()).toBe(MAX_PAGE_SIZE);
  });

  it('advances the offset by the item count, not by take', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(37, 250)));

    expect(store.skip()).toBe(37);
    expect(store.totalCount()).toBe(250);
    expect(store.hasMore()).toBe(true);
  });

  it('reports more remaining while pages are outstanding', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(100, 250, 100, 1)));

    expect(store.skip()).toBe(100);
    expect(store.hasMore()).toBe(true);
    expect(store.isFullyLoaded()).toBe(false);
  });

  it('is fully loaded once a short final page arrives', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(100, 150, 100, 1)));
    patchState(unprotected(store), setPage(page(50, 150, 100, 2)));

    expect(store.skip()).toBe(150);
    expect(store.hasMore()).toBe(false);
    expect(store.isFullyLoaded()).toBe(true);
  });

  it('is fully loaded when totalCount is an exact multiple of the page size', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(100, 200, 100, 1)));
    expect(store.hasMore()).toBe(true);

    patchState(unprotected(store), setPage(page(100, 200, 100, 2)));

    expect(store.skip()).toBe(200);
    expect(store.hasMore()).toBe(false);
    expect(store.isFullyLoaded()).toBe(true);
  });

  it('terminates on an empty page even when the total was previously higher', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(100, 200, 100, 1)));
    patchState(unprotected(store), setPage(page(0, 100, 100, 2)));

    expect(store.skip()).toBe(100);
    expect(store.totalCount()).toBe(100);
    expect(store.hasMore()).toBe(false);
  });

  it('stops a drain when an empty page arrives under a total that still claims more', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(100, 200, 100, 1)));
    // The count query and the page query disagree — rows the count sees that the
    // page does not. Adopting 200 verbatim would leave hasMore true at a stationary
    // offset, and a `while (hasMore())` drain would reissue this request forever.
    patchState(unprotected(store), setPage(page(0, 200, 100, 2)));

    expect(store.skip()).toBe(100);
    expect(store.totalCount()).toBe(100);
    expect(store.hasMore()).toBe(false);
  });

  it('reports no more remaining for an empty result set', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setFirstPage(page(0, 0)));

    expect(store.skip()).toBe(0);
    expect(store.totalCount()).toBe(0);
    expect(store.hasMore()).toBe(false);
  });

  it('caps an empty first page against a total that still claims rows', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setFirstPage(page(0, 250)));

    expect(store.skip()).toBe(0);
    expect(store.hasMore()).toBe(false);
    expect(store.isFullyLoaded()).toBe(true);
  });

  it('discards the previous offset when a fresh query replaces the first page', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setPage(page(100, 250, 100, 1)));
    patchState(unprotected(store), setPage(page(100, 250, 100, 2)));
    patchState(unprotected(store), setFirstPage(page(3, 3)));

    expect(store.skip()).toBe(3);
    expect(store.totalCount()).toBe(3);
    expect(store.hasMore()).toBe(false);
  });

  it('adopts the page size the server actually used', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setFirstPage(page(25, 25, 25)));

    expect(store.take()).toBe(25);
  });

  it('keeps the requested page size when the server reports a nonsensical one', () => {
    const store = storeWith(100);

    patchState(unprotected(store), setFirstPage(page(0, 0, 0)));

    expect(store.take()).toBe(100);
  });
});
