import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { patchState, signalStore, withFeature, withState } from '@ngrx/signals';
import { unprotected } from '@ngrx/signals/testing';
import { describe, expect, it } from 'vitest';
import {
  ClientQueryConfig,
  clearSort,
  setQuery,
  setSort,
  toggleSort,
  withClientQuery,
} from './with-client-query';

interface Model {
  readonly name: string;
  readonly provider: string;
}

type SortKey = 'name' | 'provider';

const MODELS: readonly Model[] = [
  { name: 'Sonnet', provider: 'Anthropic' },
  { name: 'GPT-5', provider: 'Azure OpenAI' },
  { name: 'Opus', provider: 'Anthropic' },
];

const CONFIG: Omit<ClientQueryConfig<Model, SortKey>, 'isAuthoritative'> = {
  searchableText: (m) => `${m.name} ${m.provider}`,
  comparators: {
    name: (a, b) => a.name.localeCompare(b.name),
    provider: (a, b) => a.provider.localeCompare(b.provider),
  },
  incompleteSetReason: 'Only the first 500 models have loaded.',
};

function storeWith(isAuthoritative: ClientQueryConfig<Model, SortKey>['isAuthoritative']) {
  const Store = signalStore(
    { providedIn: 'root' },
    withState({ models: MODELS }),
    withFeature(({ models }) => withClientQuery(models, { ...CONFIG, isAuthoritative })),
  );
  return TestBed.inject(Store);
}

function names(store: { results: () => readonly Model[] }): string[] {
  return store.results().map((m) => m.name);
}

describe('withClientQuery', () => {
  it('returns every row in source order with no query and no sort', () => {
    const store = storeWith(true);

    expect(names(store)).toEqual(['Sonnet', 'GPT-5', 'Opus']);
    expect(store.hasQuery()).toBe(false);
  });

  it('filters case-insensitively across the searchable text', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setQuery('anthropic'));

    expect(names(store)).toEqual(['Sonnet', 'Opus']);
    expect(store.hasQuery()).toBe(true);
  });

  it('treats a whitespace-only query as no query', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setQuery('   '));

    expect(names(store)).toEqual(['Sonnet', 'GPT-5', 'Opus']);
    expect(store.hasQuery()).toBe(false);
  });

  it('reports an empty result for a query nothing matches', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setQuery('llama'));

    expect(names(store)).toEqual([]);
    expect(store.isEmptyResult()).toBe(true);
  });

  it('sorts ascending and descending when the set is authoritative', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setSort('name'));
    expect(names(store)).toEqual(['GPT-5', 'Opus', 'Sonnet']);

    patchState(unprotected(store), setSort('name', 'desc'));
    expect(names(store)).toEqual(['Sonnet', 'Opus', 'GPT-5']);
  });

  it('sorts within the filtered set, not across the whole source', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setQuery('anthropic'));
    patchState(unprotected(store), setSort('name'));

    expect(names(store)).toEqual(['Opus', 'Sonnet']);
  });

  it('does not mutate the source collection while sorting', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setSort('name'));
    store.results();

    expect(store.models()).toEqual(MODELS);
  });

  it('leaves rows in server order and names the reason when not authoritative', () => {
    const store = storeWith(false);

    patchState(unprotected(store), setSort('name'));

    expect(store.canSort()).toBe(false);
    expect(names(store)).toEqual(['Sonnet', 'GPT-5', 'Opus']);
    expect(store.sortUnavailableReason()).toBe(CONFIG.incompleteSetReason);
  });

  it('still filters when sorting is unavailable, but says the result is partial', () => {
    const store = storeWith(false);

    patchState(unprotected(store), setQuery('opus'));

    expect(names(store)).toEqual(['Opus']);
    expect(store.isResultPartial()).toBe(true);
    expect(store.resultsPartialReason()).toBe(CONFIG.incompleteSetReason);
  });

  it('does not call an unfiltered list partial, however incomplete the set is', () => {
    const store = storeWith(false);

    expect(store.isResultPartial()).toBe(false);
    expect(store.resultsPartialReason()).toBeNull();
  });

  it('does not qualify a filtered result over an authoritative set', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setQuery('opus'));

    expect(store.isResultPartial()).toBe(false);
    expect(store.resultsPartialReason()).toBeNull();
  });

  it('exposes no reason while the set is authoritative', () => {
    const store = storeWith(true);

    expect(store.canSort()).toBe(true);
    expect(store.sortUnavailableReason()).toBeNull();
  });

  it('rejects a sort key the store does not offer', () => {
    const store = storeWith(true);

    // Pins the updater typing: widening SortReadableState so these compile would
    // let a store sort by a key it has no comparator for, and crash on read.
    // @ts-expect-error 'bogus' is not one of the configured comparators.
    patchState(unprotected(store), setSort('bogus'));
    // @ts-expect-error same, through the toggling form.
    patchState(unprotected(store), toggleSort('bogus'));
  });

  it('starts sorting the moment a drain makes the set authoritative', () => {
    const isFullyLoaded = signal(false);
    const store = storeWith(isFullyLoaded);

    patchState(unprotected(store), setSort('name'));
    expect(names(store)).toEqual(['Sonnet', 'GPT-5', 'Opus']);

    isFullyLoaded.set(true);

    expect(names(store)).toEqual(['GPT-5', 'Opus', 'Sonnet']);
    expect(store.sortUnavailableReason()).toBeNull();
  });

  it('flips direction when toggling the active key and resets when changing key', () => {
    const store = storeWith(true);

    patchState(unprotected(store), toggleSort('name'));
    expect(store.sortDirection()).toBe('asc');

    patchState(unprotected(store), toggleSort('name'));
    expect(store.sortDirection()).toBe('desc');
    expect(names(store)).toEqual(['Sonnet', 'Opus', 'GPT-5']);

    patchState(unprotected(store), toggleSort('provider'));
    expect(store.sortKey()).toBe('provider');
    expect(store.sortDirection()).toBe('asc');
  });

  it('returns to source order on clearSort', () => {
    const store = storeWith(true);

    patchState(unprotected(store), setSort('name'));
    patchState(unprotected(store), clearSort());

    expect(store.sortKey()).toBeNull();
    expect(names(store)).toEqual(['Sonnet', 'GPT-5', 'Opus']);
  });
});
