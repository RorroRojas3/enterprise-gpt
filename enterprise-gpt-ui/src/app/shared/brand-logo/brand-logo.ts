import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** `full` is the wordmark lockup; `mark` is the mountain alone. */
export type BrandVariant = 'full' | 'mark';

/** Intrinsic size of each generated asset, for layout reservation. */
const INTRINSIC = {
  full: { width: 512, height: 363 },
  mark: { width: 128, height: 51 },
} as const satisfies Record<BrandVariant, { width: number; height: number }>;

/**
 * The Andes wordmark or mark, in whichever variant matches the current theme.
 *
 * Both images are always rendered and one is hidden by `--show-light` /
 * `--show-dark`, so no script reads the theme to choose an asset. They carry the
 * *same* `alt`: a `display: none` element is out of the accessibility tree, so
 * exactly one is ever exposed and a screen reader never reads the wordmark twice.
 *
 * Accepted cost of the script-free swap: a hidden `<img>` is still fetched, so both
 * variants download. At ~40 kB for the pair that is the cheaper side of the trade
 * against a `background-image` version that would have to give up `alt`.
 */
@Component({
  selector: 'app-brand-logo',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './brand-logo.html',
  styleUrl: './brand-logo.scss',
})
export class BrandLogo {
  readonly variant = input<BrandVariant>('full');

  /**
   * Empty renders the lockup decoratively — correct wherever it sits beside text
   * that already names the product, such as the assistant avatar.
   */
  readonly alt = input<string>('');

  protected readonly lightSrc = computed(() => `brand/logo-${this.variant()}.webp`);
  protected readonly darkSrc = computed(() => `brand/logo-${this.variant()}-dark.webp`);
  protected readonly width = computed(() => INTRINSIC[this.variant()].width);
  protected readonly height = computed(() => INTRINSIC[this.variant()].height);
}
