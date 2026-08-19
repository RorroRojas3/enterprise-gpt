import { Injectable, Signal, TemplateRef, signal } from '@angular/core';

/**
 * What a routed screen contributes to the shell's mobile navbar. US-1403.
 *
 * Below 768px frames `1d` and `5m` draw **one** 54px bar, and its title and trailing
 * controls belong to the route rather than to the shell: chat puts the brand there with
 * its conversation kebab beside it, administration puts "Admin" there with nothing. The
 * hamburger is the shell's, because the sidebar is.
 *
 * The contract lives in `core/` for the reason `COMPOSER_HOST` does: the two halves are
 * in `features/shell` and `features/chat`, and dependencies run one way — neither
 * feature may import the other, so what they share has to sit below both.
 *
 * `ShellChrome` is **provided at the `Shell` component, never at the root**. Nothing
 * eagerly reachable imports this file, so it rides the lazy shell chunk; `providedIn:
 * 'root'` would put it on an initial graph with well under a kilobyte of headroom, to
 * hold state only a lazy component renders. For the same reason it must not be listed
 * in `app.routes.ts`, which *is* on that graph.
 */
export interface ShellChromeState {
  /** The navbar's title. `null` renders the product name frames `1d` and `5m` draw. */
  readonly title: string | null;

  /**
   * Trailing controls, rendered through `ngTemplateOutlet`. `null` renders none, which
   * is frame `5m`.
   *
   * A `TemplateRef` rather than a descriptor array, because the one real consumer is
   * the chat header's kebab: a `Menu` with a conditional item, a danger item, an
   * `aria-haspopup="dialog"` item and an anchored project picker. Encoding that as data
   * would mean the shell rebuilding the header's menu from a second description, and
   * the two drifting.
   */
  readonly actions: TemplateRef<unknown> | null;
}

/**
 * The shell's navbar slot, written by whichever screen is routed into it.
 *
 * Inject it `{ optional: true }`. A screen is perfectly renderable outside the shell —
 * every route spec does exactly that — and a required dependency would make the seam a
 * reason to rewrite those specs.
 *
 * A screen that publishes **must** clear on destroy, or the chat kebab outlives the
 * chat route and appears over the projects screen. {@link clear} is idempotent.
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
