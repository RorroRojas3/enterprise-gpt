import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { PREFERENCE_KEYS, readPreference, writePreference } from '@core/storage/local-preferences';

const COLLAPSED = 'collapsed';
const EXPANDED = 'expanded';

interface UiState {
  readonly sidebarCollapsed: boolean;
}

function readStoredCollapsed(): boolean {
  // Anything other than the collapsed marker — unset, corrupted, or storage the
  // browser refuses to open — means expanded, which is the state that shows the user
  // where everything is.
  return readPreference(PREFERENCE_KEYS.sidebar) === COLLAPSED;
}

/**
 * Layout choices that belong to this browser rather than to the signed-in user.
 *
 * It deliberately does **not** compose `withResetOnSignOut`. The rule stated in
 * `core/storage/local-preferences.ts` is that `localStorage` holds only what is about
 * the machine, not the person — and US-205 requires the sidebar preference to be one
 * of exactly two things that survive sign-out, alongside the theme.
 *
 * No pre-paint script is needed for the width the way `index.html` needs one for the
 * theme: the startup shell covers the page until `bootstrapApplication` resolves, so
 * this store is constructed before anything of the app is painted. What the shell does
 * need is to withhold its width *transition* until after the first render, or a
 * restored collapsed sidebar would animate open-to-closed on every load.
 *
 * US-910's device-local project pins land here too, which is why this is a store
 * rather than a one-field service.
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
