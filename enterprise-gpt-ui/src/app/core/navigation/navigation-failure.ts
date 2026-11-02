import { inject } from '@angular/core';
import { ToastStore } from '@core/notifications/toast-store';
import { HardNavigation } from './hard-navigation';

/**
 * For `withNavigationErrorHandler`, usually a lazy chunk a new deployment replaced. Only a reload
 * fetches the new one, and the router would otherwise fail in silence.
 */
export function notifyNavigationFailure(): void {
  const navigation = inject(HardNavigation);

  inject(ToastStore).show({
    tone: 'error',
    title: 'That page could not be opened.',
    detail: 'Reload to get the latest version of Enterprise GPT.',
    action: { label: 'Reload', run: () => navigation.reload() },
    key: 'navigation-failure',
  });
}
