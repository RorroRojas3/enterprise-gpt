import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Four variants cover the boards' seven paths: `viewBox` scaling reproduces the sizes. */
export type RidgelineVariant = 'divider' | 'thinking' | 'empty' | 'auth';

/** Inline SVG, not an image, so the stroke re-tints with the theme's `--muted` and `--accent`. */
@Component({
  selector: 'app-ridgeline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ridgeline.html',
  styleUrl: './ridgeline.scss',
  host: { '[attr.data-variant]': 'variant()' },
})
export class Ridgeline {
  readonly variant = input<RidgelineVariant>('empty');
}
