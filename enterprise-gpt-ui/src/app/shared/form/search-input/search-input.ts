import {
  ChangeDetectionStrategy,
  Component,
  effect,
  input,
  linkedSignal,
  model,
  output,
} from '@angular/core';
import { Icon } from '@shared/icon/icon';

let nextId = 0;

/**
 * A debounced search field — the sidebar, both libraries, and every admin tab.
 *
 * The label is required and rendered as a real `<label for>`. A placeholder is not an
 * accessible name: it disappears the moment the user types, and several screen readers
 * do not announce it at all.
 *
 * Debouncing uses `setTimeout` inside an `effect` with `onCleanup` rather than an
 * rxjs pipeline: it needs no `rxjs-interop` import, and `vi.useFakeTimers()` drives it
 * directly in tests.
 */
@Component({
  selector: 'app-search-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './search-input.html',
  styleUrl: './search-input.scss',
})
export class SearchInput {
  /** The committed value, updated after the debounce elapses. */
  readonly value = model<string>('');

  readonly label = input.required<string>();
  readonly placeholder = input<string>('');
  readonly debounceMs = input<number>(300);

  /** True while a request driven by this field is in flight. */
  readonly busy = input<boolean>(false);

  /** Fires with the committed value — the same moment `value` changes. */
  readonly searched = output<string>();

  protected readonly inputId = `search-input-${nextId++}`;

  /**
   * What is in the field right now, before the debounce commits it.
   *
   * `linkedSignal` rather than an effect writing a signal: it is writable state
   * derived from `value`, which is exactly what `linkedSignal` is for. Resetting
   * `value` from outside — clearing a filter from an empty state, restoring a query
   * from the URL — puts the field back in step with no extra pass.
   */
  protected readonly text = linkedSignal(() => this.value());

  constructor() {
    effect((onCleanup) => {
      const next = this.text();
      const timer = setTimeout(() => {
        if (next !== this.value()) {
          this.value.set(next);
          this.searched.emit(next);
        }
      }, this.debounceMs());

      // Every keystroke replaces the pending timer, which is what makes this a
      // debounce rather than a queue of stale searches.
      onCleanup(() => clearTimeout(timer));
    });
  }

  protected onInput(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
  }

  /** Escape clears — the shortcut every search field is expected to have. */
  protected onEscape(): void {
    this.text.set('');
  }
}
