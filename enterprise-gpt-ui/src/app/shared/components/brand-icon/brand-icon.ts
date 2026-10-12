import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  isDevMode,
} from '@angular/core';
import { BRAND_ICON_NAME_SET, BrandIconName } from '@shared/utils/icons/brand-icon-names';

/**
 * A multi-colour brand mark from the sprite. A sibling of {@link Icon}, not a widening of it:
 * `icon.scss` forces `fill: currentColor`, which a brand mark must not inherit.
 */
@Component({
  selector: 'app-brand-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './brand-icon.html',
  styleUrl: './brand-icon.scss',
})
export class BrandIcon {
  /** Checked against the brand manifest at compile time by `strictTemplates`. */
  readonly name = input.required<BrandIconName>();

  /**
   * Promotes the mark from decoration to content. Leave it unset beside text that already names
   * the thing, or it is announced twice.
   */
  readonly label = input<string>();

  protected readonly href = computed(() => `#${this.name()}`);
  protected readonly decorative = computed(() => this.label() === undefined);

  constructor() {
    if (isDevMode()) {
      // Catches run-time names (`mcpBrandIcon`'s), which the type can't; a miss renders blank.
      effect(() => {
        const name: string = this.name();
        if (!BRAND_ICON_NAME_SET.has(name)) {
          console.error(
            `<app-brand-icon> was given "${name}", which is not in the brand manifest ` +
              `(src/app/shared/utils/icons/brand-icon-names.ts). It will render blank.`,
          );
        }
      });
    }
  }
}
