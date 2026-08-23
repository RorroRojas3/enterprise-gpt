import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The on/off visual of a switch, and nothing else.
 *
 * **It carries no role and is always `aria-hidden`.** The semantics belong to
 * whatever wraps it — a `menuitemcheckbox` button, a `role="switch"` button, a
 * `<label>` over a real input. That split is what lets a switch appear inside a
 * `role="menu"` panel at all: a native `<input type="checkbox" role="switch">`
 * is not a valid child of `role="menu"`, so the row's button owns the state and
 * this only draws it.
 *
 * Where a real form control is available, use Bootstrap's own
 * `.form-check.form-switch` with `[formField]` instead — its `form-check`
 * partial is already in the bundle. This matches that control's geometry so the
 * two read as one idiom.
 *
 * @example
 * <button role="menuitemcheckbox" [attr.aria-checked]="on()" (click)="toggle()">
 *   <span>Label</span>
 *   <app-switch [checked]="on()" />
 * </button>
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
