import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type StatusTone =
  | 'ok'
  | 'warn'
  | 'fail'
  | 'muted'
  | 'accent'
  | 'brand'
  | 'provider-azure-openai'
  | 'provider-bedrock'
  | 'provider-anthropic';

/**
 * A coloured dot with its meaning beside it. Colour is never the only channel (WCAG 1.4.1):
 * `labelHidden` makes the label screen-reader-only rather than removing it.
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
