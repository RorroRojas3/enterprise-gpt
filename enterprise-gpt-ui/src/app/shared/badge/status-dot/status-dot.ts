import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type StatusTone = 'ok' | 'warn' | 'fail' | 'muted' | 'accent' | 'brand';

/**
 * A coloured dot with its meaning beside it — provider dots in the model picker,
 * activity state in the admin tables.
 *
 * The label is required and visible by default. Colour is never the only channel
 * (WCAG 1.4.1): `labelHidden` moves the text to the screen-reader-only class rather
 * than removing it, so the meaning survives for someone who cannot see the hue.
 */
@Component({
  selector: 'app-status-dot',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './status-dot.html',
  styleUrl: './status-dot.scss',
})
export class StatusDot {
  readonly tone = input<StatusTone>('muted');
  readonly label = input.required<string>();
  readonly labelHidden = input<boolean>(false);
  readonly size = input<number>(7);
}
