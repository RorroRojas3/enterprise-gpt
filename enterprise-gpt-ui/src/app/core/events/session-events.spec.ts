import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SIGNED_OUT_ABORT_REASON, injectSignedOutAbort, sessionEvents } from './session-events';

describe('injectSignedOutAbort', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
  });

  function mintFactory(): () => AbortSignal {
    return TestBed.runInInjectionContext(() => injectSignedOutAbort());
  }

  function signOut(): void {
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
  }

  it('aborts every outstanding signal when the session ends', () => {
    const mint = mintFactory();
    const first = mint();
    const second = mint();

    expect(first.aborted).toBe(false);

    signOut();

    expect(first.aborted).toBe(true);
    expect(second.aborted).toBe(true);
    expect(first.reason).toBe(SIGNED_OUT_ABORT_REASON);
  });

  it('mints one signal per request rather than sharing one', () => {
    // Sharing a single signal makes two concurrent requests indistinguishable, and ties
    // every component's Stop to the same object.
    const mint = mintFactory();

    expect(mint()).not.toBe(mint());
  });

  it('composes with a caller-owned Stop', () => {
    const mint = mintFactory();
    const stop = new AbortController();
    const combined = AbortSignal.any([stop.signal, mint()]);

    signOut();

    expect(combined.aborted).toBe(true);
  });

  it('is already aborted for anything started after the session ended', () => {
    // Correct rather than incidental: sign-in is always a redirect, so a session never
    // resumes in the same document and work starting now is doomed either way. It
    // surfaces as an `aborted` AppError, which raises no toast.
    const mint = mintFactory();

    signOut();

    expect(mint().aborted).toBe(true);
  });

  it('aborts when the injection context is destroyed, so a stream cannot outlive its store', () => {
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    const mint = runInInjectionContext(injector, () => injectSignedOutAbort());
    const signal = mint();
    const onAbort = vi.fn();
    signal.addEventListener('abort', onAbort);

    injector.destroy();

    expect(signal.aborted).toBe(true);
    expect(onAbort).toHaveBeenCalledOnce();
  });
});
