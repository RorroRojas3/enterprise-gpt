import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  isDevMode,
} from '@angular/core';
import { ICON_NAME_SET, IconName } from './icon-names';

/** One glyph from the sprite, sized in `em` so callers set its size with `font-size`. */
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
   * Promotes the icon from decoration to content. Leave it unset beside text that already names
   * the control, or it is announced twice.
   */
  readonly label = input<string>();

  protected readonly href = computed(() => `#${this.name()}`);
  protected readonly decorative = computed(() => this.label() === undefined);

  constructor() {
    if (isDevMode()) {
      // Catches run-time names (`'bi-' + kind`), which the type can't; a miss renders blank.
      effect(() => {
        const name: string = this.name();
        if (!ICON_NAME_SET.has(name)) {
          console.error(
            `<app-icon> was given "${name}", which is not in the sprite manifest ` +
              `(src/app/shared/icons/icon-names.ts). It will render blank.`,
          );
        }
      });
    }
  }
}
