import { EnvironmentInjector, createEnvironmentInjector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { wouldPromptOnUnload as wouldPrompt } from '@testing/unload-prompt';
import { UnloadPrompt } from './unload-prompt';

describe('UnloadPrompt', () => {
  let prompt: UnloadPrompt;
  let caller: EnvironmentInjector;

  beforeEach(() => {
    TestBed.resetTestingModule();
    prompt = TestBed.inject(UnloadPrompt);
    caller = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
  });

  function register(busy: ReturnType<typeof signal<boolean>>): void {
    caller.runInContext(() => prompt.register(busy));
    TestBed.tick();
  }

  it('asks only while a registered source is busy', () => {
    const busy = signal(false);
    register(busy);
    expect(wouldPrompt()).toBe(false);

    busy.set(true);
    TestBed.tick();
    expect(wouldPrompt()).toBe(true);

    busy.set(false);
    TestBed.tick();
    expect(wouldPrompt()).toBe(false);
  });

  it('forgets a source once its caller is destroyed', () => {
    register(signal(true));
    expect(wouldPrompt()).toBe(true);

    caller.destroy();
    TestBed.tick();

    expect(wouldPrompt()).toBe(false);
  });

  it('stays quiet for a leave the app started itself', () => {
    register(signal(true));

    prompt.suspend();

    expect(wouldPrompt()).toBe(false);
  });

  it('lets a redirect leave unprompted, and guards again when it fails', async () => {
    register(signal(true));
    let duringLeave: boolean | null = null;

    await expect(
      prompt.leaving(() => {
        duringLeave = wouldPrompt();
        return Promise.reject(new Error('Entra unreachable'));
      }),
    ).rejects.toThrow('Entra unreachable');

    expect(duringLeave).toBe(false);
    // The page stayed, so the work in flight is still at risk.
    expect(wouldPrompt()).toBe(true);
  });
});
