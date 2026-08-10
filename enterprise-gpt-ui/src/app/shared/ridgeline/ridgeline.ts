import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Where the ridgeline appears. The design boards draw seven hand-tuned path pairs;
 * these four cover every one of them, because `viewBox` scaling reproduces the size
 * variants and the stroke thins with them, which is what the boards do by hand.
 */
export type RidgelineVariant = 'divider' | 'thinking' | 'empty' | 'auth';

/**
 * The Andes ridgeline motif — the sidebar divider, the thinking indicator, and the
 * illustration on every empty and auth state.
 *
 * Inline SVG rather than an image file, so the stroke inherits `--muted` and
 * `--accent` and re-tints on a theme change with no second asset and no script.
 */
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
