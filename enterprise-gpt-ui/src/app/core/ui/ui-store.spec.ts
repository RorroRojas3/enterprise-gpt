import { TestBed } from '@angular/core/testing';
import { sessionEvents } from '@core/events/session-events';
import { PREFERENCE_KEYS, writePreference } from '@core/storage/local-preferences';
import { Dispatcher } from '@ngrx/signals/events';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { UiStore } from './ui-store';

describe('UiStore', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('opens expanded when nothing has been stored', () => {
    expect(TestBed.inject(UiStore).sidebarCollapsed()).toBe(false);
  });

  it('restores a collapsed sidebar from the stored preference', () => {
    // US-301: the preference has to be in force before the first paint, which is why
    // it is read during construction rather than in a hook.
    writePreference(PREFERENCE_KEYS.sidebar, 'collapsed');

    expect(TestBed.inject(UiStore).sidebarCollapsed()).toBe(true);
  });

  it('persists the choice under the one allowlisted key', () => {
    const store = TestBed.inject(UiStore);

    store.toggleSidebar();

    expect(store.sidebarCollapsed()).toBe(true);
    expect(localStorage.getItem(PREFERENCE_KEYS.sidebar)).toBe('collapsed');

    store.toggleSidebar();

    expect(store.sidebarCollapsed()).toBe(false);
    expect(localStorage.getItem(PREFERENCE_KEYS.sidebar)).toBe('expanded');
  });

  it('treats an unrecognised stored value as expanded', () => {
    // A corrupted value, or storage the browser refuses to open, must not leave a user
    // looking at a 60px strip they never chose.
    writePreference(PREFERENCE_KEYS.sidebar, 'whatever this is');

    expect(TestBed.inject(UiStore).sidebarCollapsed()).toBe(false);
  });

  it('keeps the sidebar preference through sign-out', () => {
    // US-205's second criterion: theme and sidebar survive, everything else goes. This
    // store deliberately does not compose withResetOnSignOut — the preference is about
    // the browser, not the person.
    const store = TestBed.inject(UiStore);
    store.setSidebarCollapsed(true);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());

    expect(store.sidebarCollapsed()).toBe(true);
    expect(localStorage.getItem(PREFERENCE_KEYS.sidebar)).toBe('collapsed');
  });
});
