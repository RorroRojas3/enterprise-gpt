import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  linkedSignal,
  model,
  output,
  viewChild,
} from '@angular/core';
import { Icon } from '@shared/icons/icon';

let nextId = 0;

/**
 * The label is a real `<label for>`, as a placeholder is no accessible name. Debounced with
 * `setTimeout` in an effect, not rxjs, so `vi.useFakeTimers()` drives it directly.
 */
@Component({
  selector: 'app-search-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  host: { class: 'd-block' },
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

  /** The uncommitted field text; resetting `value` from outside resyncs it with no extra pass. */
  protected readonly text = linkedSignal(() => this.value());

  private readonly _field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  /** For callers that reveal the field, such as the collapsed sidebar's search button. */
  focusField(): void {
    this._field().nativeElement.focus();
  }

  constructor() {
    effect((onCleanup) => {
      const next = this.text();
      const timer = setTimeout(() => {
        if (next !== this.value()) {
          this.value.set(next);
          this.searched.emit(next);
        }
      }, this.debounceMs());

      onCleanup(() => clearTimeout(timer));
    });
  }

  protected onInput(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
  }

  protected onEscape(): void {
    this.text.set('');
  }
}
