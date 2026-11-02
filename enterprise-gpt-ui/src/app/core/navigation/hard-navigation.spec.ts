import { DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { HardNavigation } from './hard-navigation';
import { UnloadPrompt } from './unload-prompt';

describe('HardNavigation', () => {
  const location = { assign: vi.fn(), reload: vi.fn() };
  let suspend: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    location.assign.mockClear();
    location.reload.mockClear();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      // jsdom cannot patch `Location`, so the window is a stand-in.
      providers: [
        {
          provide: DOCUMENT,
          useValue: {
            baseURI: 'https://app.example/gpt/',
            defaultView: { location, addEventListener: vi.fn(), removeEventListener: vi.fn() },
          },
        },
      ],
    });
    suspend = vi.spyOn(TestBed.inject(UnloadPrompt), 'suspend');
  });

  it('stands the leave guard down before it replaces the document', () => {
    TestBed.inject(HardNavigation).assign('/signed-out');

    expect(location.assign).toHaveBeenCalledWith('https://app.example/gpt/signed-out');
    expect(suspend.mock.invocationCallOrder[0]).toBeLessThan(
      location.assign.mock.invocationCallOrder[0]!,
    );
  });

  it('stands the leave guard down before it reloads', () => {
    TestBed.inject(HardNavigation).reload();

    expect(location.reload).toHaveBeenCalledOnce();
    expect(suspend.mock.invocationCallOrder[0]).toBeLessThan(
      location.reload.mock.invocationCallOrder[0]!,
    );
  });
});
