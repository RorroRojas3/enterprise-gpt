import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The on/off visual of a switch, always `aria-hidden`: the wrapper owns the semantics, which lets
 * it sit in a `role="menu"` panel where a native switch input cannot. Where a real form control
 * fits, use Bootstrap's `.form-check.form-switch` instead.
 */
@Component({
  selector: 'app-switch',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './switch.html',
  styleUrl: './switch.scss',
})
export class Switch {
  readonly checked = input.required<boolean>();
}
