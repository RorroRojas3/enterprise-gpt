import {
  DOCUMENT,
  DestroyRef,
  Injectable,
  Signal,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';

/**
 * Asks before leaving while work would be lost. App-initiated leaves go through {@link leaving} or
 * {@link suspend}: a prompt cancelled mid-MSAL-redirect strands its interaction lock.
 */
@Injectable({ providedIn: 'root' })
export class UnloadPrompt {
  private readonly _window = inject(DOCUMENT).defaultView;
  private readonly _sources = signal<readonly Signal<boolean>[]>([]);
  private _suspended = false;

  private readonly _onBeforeUnload = (event: BeforeUnloadEvent): void => {
    if (!this._suspended) {
      event.preventDefault();
    }
  };

  private readonly _busy = computed(() => this._sources().some((source) => source()));

  constructor() {
    // Attached only while busy: any `beforeunload` listener keeps the page out of the bfcache.
    effect((onCleanup) => {
      const target = this._window;
      if (!this._busy() || target === null) {
        return;
      }

      target.addEventListener('beforeunload', this._onBeforeUnload);
      onCleanup(() => target.removeEventListener('beforeunload', this._onBeforeUnload));
    });
  }

  /** Guards while `busy` is true, until the caller's injector is destroyed. */
  register(busy: Signal<boolean>): void {
    this._sources.update((sources) => [...sources, busy]);
    inject(DestroyRef).onDestroy(() =>
      this._sources.update((sources) => sources.filter((source) => source !== busy)),
    );
  }

  /** For a leave that cannot fail, such as `location.assign`. */
  suspend(): void {
    this._suspended = true;
  }

  /** Runs a redirect unprompted. A rejection means the page stayed, so the guard returns. */
  async leaving<T>(leave: () => Promise<T>): Promise<T> {
    this._suspended = true;
    try {
      return await leave();
    } catch (cause) {
      this._suspended = false;
      throw cause;
    }
  }
}
