import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** `full` is the wordmark lockup; `mark` is the mountain alone. */
export type BrandVariant = 'full' | 'mark';

/** Intrinsic size of each generated asset, for layout reservation. */
const INTRINSIC = {
  full: { width: 512, height: 363 },
  mark: { width: 128, height: 51 },
} as const satisfies Record<BrandVariant, { width: number; height: number }>;

/**
 * Both theme variants render, toggled by `--show-light`/`--show-dark`, with the same `alt`, since
 * only the visible one is exposed. Both download (~40 kB): the price of keeping `alt`.
 */
@Component({
  selector: 'app-brand-logo',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './brand-logo.html',
  styleUrl: './brand-logo.scss',
})
export class BrandLogo {
  readonly variant = input<BrandVariant>('full');

  /** Empty renders it decoratively, for spots beside text that already names the product. */
  readonly alt = input<string>('');

  protected readonly lightSrc = computed(() => `brand/logo-${this.variant()}.webp`);
  protected readonly darkSrc = computed(() => `brand/logo-${this.variant()}-dark.webp`);
  protected readonly width = computed(() => INTRINSIC[this.variant()].width);
  protected readonly height = computed(() => INTRINSIC[this.variant()].height);
}
