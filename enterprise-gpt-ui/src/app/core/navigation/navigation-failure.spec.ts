import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ToastStore } from '@core/notifications/toast-store';
import { FakeNavigation, provideFakeNavigation } from '@testing/navigation';
import { notifyNavigationFailure } from './navigation-failure';

describe('notifyNavigationFailure', () => {
  let navigation: FakeNavigation;

  beforeEach(() => {
    const fake = provideFakeNavigation();
    navigation = fake.useValue;
    TestBed.configureTestingModule({ providers: [fake] });
  });

  it('offers a reload, the only way to fetch a chunk a deployment replaced', () => {
    TestBed.runInInjectionContext(notifyNavigationFailure);

    const store = TestBed.inject(ToastStore);
    const toast = store.toasts()[0]!;
    expect(toast.tone).toBe('error');
    expect(toast.actionLabel).toBe('Reload');

    store.runAction(toast.id);

    expect(navigation.reload).toHaveBeenCalledOnce();
  });

  it('says so once, however many navigations fail', () => {
    TestBed.runInInjectionContext(notifyNavigationFailure);
    TestBed.runInInjectionContext(notifyNavigationFailure);

    expect(TestBed.inject(ToastStore).toasts()).toHaveLength(1);
  });
});
