import { Injectable, Signal, TemplateRef, signal } from '@angular/core';

/**
 * A routed screen's title and trailing controls for the shell's mobile navbar. In `core/` so
 * shell and chat share it without importing each other. Provided at `Shell`, never at root or
 * in `app.routes.ts`, to keep it off the initial bundle.
 */
export interface ShellChromeState {
  /** `null` renders the product name. */
  readonly title: string | null;

  /**
   * Trailing controls; `null` renders none. A `TemplateRef` rather than data, so the chat kebab's
   * menu has one description rather than two that drift.
   */
  readonly actions: TemplateRef<unknown> | null;
}

/**
 * Inject it `{ optional: true }`: route specs render screens outside the shell. A publisher must
 * {@link clear} on destroy, or the chat kebab outlives the chat route.
 */
@Injectable()
export class ShellChrome {
  private readonly _state = signal<ShellChromeState>({ title: null, actions: null });

  readonly state: Signal<ShellChromeState> = this._state.asReadonly();

  /** Replaces the whole slot. Fields left out revert to their empty value. */
  publish(state: Partial<ShellChromeState>): void {
    this._state.set({ title: state.title ?? null, actions: state.actions ?? null });
  }

  clear(): void {
    this._state.set({ title: null, actions: null });
  }
}
