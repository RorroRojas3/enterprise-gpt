import { ChangeDetectionStrategy, Component, ElementRef, viewChild } from '@angular/core';
import { focusOnRender } from '@shared/a11y/focus-on-render';
import { Ridgeline } from '@shared/ridgeline/ridgeline';

/**
 * A component so `focusOnRender` sees a real heading, not inline in `App`. A full page, as it can
 * sit through MSAL's 30 s redirect timeout; unrouted, because sign-out must not navigate.
 */
@Component({
  selector: 'app-signing-out',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Ridgeline],
  templateUrl: './signing-out.html',
  styleUrl: './signing-out.scss',
})
export class SigningOut {
  private readonly _heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  constructor() {
    focusOnRender(this._heading);
  }
}
