import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppError } from '@core/errors/app-error';
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

  it.each(['success', 'info', 'warning', 'error'] as const)(
    'dismisses a %s toast at exactly three seconds',
    (tone) => {
      const store = TestBed.inject(ToastStore);
      store.show({ tone, title: 'Something happened' });

      vi.advanceTimersByTime(2999);
      expect(store.toasts()).toHaveLength(1);

      vi.advanceTimersByTime(1);
      expect(store.toasts()).toHaveLength(0);
    },
  );

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

    store.fromError(appError(), () => {});

    const toast = store.toasts()[0];
    expect(toast?.tone).toBe('error');
    expect(toast?.title).toBe('That item no longer exists, or you do not have access to it.');
    expect(toast?.traceLine).toBe('Trace ID: 4c19-e8d2-90ab-114f');
    expect(toast?.actionLabel).toBe('Retry');
  });

  it('renders no Retry affordance without an action behind it', () => {
    const store = TestBed.inject(ToastStore);

    store.show({ tone: 'error', title: 'Failed', retryLabel: 'Retry' });

    // A label with no handler would render a control that does nothing when pressed.
    expect(store.toasts()[0]?.actionLabel).toBeNull();
  });

  it('runs the retry and dismisses the toast that offered it', () => {
    const store = TestBed.inject(ToastStore);
    const onRetry = vi.fn();

    const id = store.show({ tone: 'error', title: 'Failed', onRetry });
    store.runAction(id);

    expect(onRetry).toHaveBeenCalledTimes(1);
    // Dismissed first, so a handler that raises its own toast does not stack under
    // the failure it is retrying.
    expect(store.toasts()).toHaveLength(0);
  });

  it('forgets a retry handler once its toast is gone', () => {
    const store = TestBed.inject(ToastStore);
    const onRetry = vi.fn();

    const id = store.show({ tone: 'error', title: 'Failed', onRetry });
    store.dismiss(id);
    store.runAction(id);

    expect(onRetry).not.toHaveBeenCalled();
  });

  it('renders an action in place of Retry, and runs it after dismissing', () => {
    const store = TestBed.inject(ToastStore);
    const run = vi.fn();
    const onRetry = vi.fn();

    const id = store.show({
      tone: 'info',
      title: 'Answer ready',
      action: { label: 'Open', run },
      onRetry,
    });
    expect(store.toasts()[0]?.actionLabel).toBe('Open');

    store.runAction(id);

    expect(run).toHaveBeenCalledTimes(1);
    expect(onRetry).not.toHaveBeenCalled();
    expect(store.toasts()).toHaveLength(0);
  });

  it('replaces a live toast with the same key rather than stacking it', () => {
    const store = TestBed.inject(ToastStore);

    store.show({ tone: 'warning', title: 'First', key: 'cap' });
    store.show({ tone: 'warning', title: 'Second', key: 'cap' });
    store.show({ tone: 'warning', title: 'Unkeyed' });

    expect(store.toasts().map((toast) => toast.title)).toEqual(['Second', 'Unkeyed']);

    store.dismissKey('cap');
    expect(store.toasts().map((toast) => toast.title)).toEqual(['Unkeyed']);
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
