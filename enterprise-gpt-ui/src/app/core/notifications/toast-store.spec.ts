import { TestBed } from '@angular/core/testing';
import { AppError } from '@core/errors/app-error';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastStore } from './toast-store';

function appError(overrides: Partial<AppError> = {}): AppError {
  return {
    kind: 'resource-not-found',
    status: 404,
    title: 'Not Found',
    detail: null,
    traceId: '4c19-e8d2-90ab-114f',
    instance: null,
    ...overrides,
  } as AppError;
}

describe('ToastStore', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('auto-dismisses a success toast at exactly five seconds', () => {
    const store = TestBed.inject(ToastStore);
    store.success('Conversation deleted');

    vi.advanceTimersByTime(4999);
    expect(store.toasts()).toHaveLength(1);

    vi.advanceTimersByTime(1);
    expect(store.toasts()).toHaveLength(0);
  });

  it('keeps an error toast until it is dismissed — the whole point of the split', () => {
    const store = TestBed.inject(ToastStore);
    store.show({ tone: 'error', title: 'Upload failed' });

    vi.advanceTimersByTime(60_000);

    expect(store.toasts()).toHaveLength(1);
  });

  it('keeps a warning toast too', () => {
    const store = TestBed.inject(ToastStore);
    store.warning('Sorting is unavailable past the ceiling');

    vi.advanceTimersByTime(60_000);

    expect(store.toasts()).toHaveLength(1);
  });

  it('clears the timer when a toast is dismissed early', () => {
    const store = TestBed.inject(ToastStore);
    const id = store.success('Saved');

    store.dismiss(id);

    expect(store.toasts()).toHaveLength(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('leaves no timers behind after dismissAll', () => {
    const store = TestBed.inject(ToastStore);
    store.success('One');
    store.info('Two');

    store.dismissAll();

    expect(store.toasts()).toHaveLength(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('splits toasts by how urgently they should be announced', () => {
    const store = TestBed.inject(ToastStore);
    store.success('Saved');
    store.info('Heads up');
    store.warning('Careful');
    store.show({ tone: 'error', title: 'Failed' });

    expect(store.politeToasts().map((toast) => toast.tone)).toEqual(['success', 'info']);
    expect(store.assertiveToasts().map((toast) => toast.tone)).toEqual(['warning', 'error']);
  });

  it('turns an AppError into a toast carrying its user message and trace line', () => {
    const store = TestBed.inject(ToastStore);

    store.fromError(appError(), 'Retry');

    const toast = store.toasts()[0];
    expect(toast?.tone).toBe('error');
    expect(toast?.title).toBe('That item no longer exists, or you do not have access to it.');
    expect(toast?.traceLine).toBe('Trace ID: 4c19-e8d2-90ab-114f');
    expect(toast?.retryLabel).toBe('Retry');
  });

  it('says so honestly when the failure carried no trace id', () => {
    const store = TestBed.inject(ToastStore);

    store.fromError(appError({ traceId: null }));

    expect(store.toasts()[0]?.traceLine).toBe('No trace ID was returned.');
  });

  it('stays silent for a cancelled turn — the user pressed Stop', () => {
    const store = TestBed.inject(ToastStore);

    const id = store.fromError({
      kind: 'aborted',
      message: 'The request was cancelled.',
      traceId: null,
    } as AppError);

    expect(id).toBeNull();
    expect(store.toasts()).toHaveLength(0);
  });

  it('stays silent for a validation error, which renders at the fields', () => {
    const store = TestBed.inject(ToastStore);

    const id = store.fromError({
      kind: 'validation-error',
      status: 400,
      errors: {},
      traceId: null,
    } as AppError);

    expect(id).toBeNull();
  });
});
