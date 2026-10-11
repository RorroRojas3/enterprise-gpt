import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { PREFERENCE_KEYS, readPreference, writePreference } from '@core/storage/local-preferences';

const COLLAPSED = 'collapsed';
const EXPANDED = 'expanded';

interface UiState {
  readonly sidebarCollapsed: boolean;
}

function readStoredCollapsed(): boolean {
  // Anything but the collapsed marker means expanded, the state that shows where everything is.
  return readPreference(PREFERENCE_KEYS.sidebar) === COLLAPSED;
}

/**
 * Browser-local layout, so it skips `withResetOnSignOut`. Needs no pre-paint script: the startup
 * shell covers the page until bootstrap. The shell must still defer its width transition past
 * the first render, or a restored collapse animates on every load.
 */
export const UiStore = signalStore(
  { providedIn: 'root' },
  withState<UiState>(() => ({ sidebarCollapsed: readStoredCollapsed() })),
  withMethods((store) => {
    function setSidebarCollapsed(sidebarCollapsed: boolean): void {
      patchState(store, { sidebarCollapsed });
      writePreference(PREFERENCE_KEYS.sidebar, sidebarCollapsed ? COLLAPSED : EXPANDED);
    }

    return {
      setSidebarCollapsed,
      toggleSidebar(): void {
        setSidebarCollapsed(!store.sidebarCollapsed());
      },
    };
  }),
);
