import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  isDevMode,
} from '@angular/core';
import { ICON_NAME_SET, IconName } from './icon-names';

/**
 * One glyph from the sprite `scripts/build-icon-sprite.mjs` emits and
 * `loadIconSprite()` injects into the document.
 *
 * Sized in `em` deliberately: every design board sets icon size with `font-size`,
 * so callers keep doing exactly that and no size input is needed.
 *
 * @example
 * <app-icon name="bi-search" />                          <!-- decorative -->
 * <app-icon name="bi-trash3" label="Delete" />           <!-- content -->
 */
@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './icon.html',
  styleUrl: './icon.scss',
})
export class Icon {
  /** Checked against the sprite manifest at compile time by `strictTemplates`. */
  readonly name = input.required<IconName>();

  /**
   * Supplying a label promotes the icon from decoration to content. Leave it unset
   * whenever the icon sits beside text that already names the control — a labelled
   * icon inside a labelled button is announced twice.
   */
  readonly label = input<string>();

  protected readonly href = computed(() => `#${this.name()}`);
  protected readonly decorative = computed(() => this.label() === undefined);

  constructor() {
    if (isDevMode()) {
      // The type covers every static reference. It cannot cover a name assembled at
      // run time (`'bi-' + kind`), which renders as an empty box with no error, so
      // that one case is caught here instead.
      effect(() => {
        const name: string = this.name();
        if (!ICON_NAME_SET.has(name)) {
          console.error(
            `<app-icon> was given "${name}", which is not in the sprite manifest ` +
              `(src/app/shared/icon/icon-names.ts). It will render blank.`,
          );
        }
      });
    }
  }
}
