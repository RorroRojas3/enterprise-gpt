import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  isDevMode,
} from '@angular/core';
import { BRAND_ICON_NAME_SET, BrandIconName } from './brand-icon-names';

/**
 * One multi-colour brand mark from the sprite that
 * `scripts/build-icon-sprite.mjs` emits and `loadIconSprite()` injects.
 *
 * A sibling of {@link Icon} rather than a widening of it: `icon.scss` forces
 * `fill: currentColor`, which is exactly what a brand mark must not inherit, and
 * the two manifests stay independently checkable this way.
 *
 * Sized in `em` like every other icon here, so callers set size with
 * `font-size`.
 *
 * @example
 * <app-brand-icon name="brand-microsoft" />                    <!-- decorative -->
 * <app-brand-icon name="brand-context7" label="Context7" />    <!-- content -->
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
   * Supplying a label promotes the mark from decoration to content. Leave it
   * unset whenever it sits beside text that already names the thing — a labelled
   * mark beside its own name is announced twice.
   */
  readonly label = input<string>();

  protected readonly href = computed(() => `#${this.name()}`);
  protected readonly decorative = computed(() => this.label() === undefined);

  constructor() {
    if (isDevMode()) {
      // The type covers every static reference. It cannot cover a name resolved
      // at run time — which is exactly how `mcpBrandIcon` supplies this one —
      // and a miss renders as an empty box with no error.
      effect(() => {
        const name: string = this.name();
        if (!BRAND_ICON_NAME_SET.has(name)) {
          console.error(
            `<app-brand-icon> was given "${name}", which is not in the brand manifest ` +
              `(src/app/shared/icon/brand-icon-names.ts). It will render blank.`,
          );
        }
      });
    }
  }
}
