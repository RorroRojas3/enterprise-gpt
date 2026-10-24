import { ChangeDetectionStrategy, Component, ElementRef, viewChild } from '@angular/core';
import { focusOnRender } from '@core/a11y/focus-on-render';
import { Ridgeline } from '@shared/brand/ridgeline';

/**
 * A component so `focusOnRender` sees a real heading, not inline in `App`. A full page, as it can
 * sit through MSAL's 30 s redirect timeout; unrouted, because sign-out must not navigate.
 */
@Component({
  selector: 'app-signing-out',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Ridgeline],
  host: {
    class:
      'd-flex flex-column align-items-center justify-content-center gap-3 min-dvh-100 p-4 text-center',
  },
  templateUrl: './signing-out.html',
})
export class SigningOut {
  private readonly _heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  constructor() {
    focusOnRender(this._heading);
  }
}
