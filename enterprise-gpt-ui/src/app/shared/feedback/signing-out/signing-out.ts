import { ChangeDetectionStrategy, Component, ElementRef, viewChild } from '@angular/core';
import { focusOnRender } from '@shared/a11y/focus-on-render';
import { Ridgeline } from '@shared/ridgeline/ridgeline';

/**
 * The full-page state that replaces the shell while sign-out is under way.
 *
 * A component rather than markup inline in `App`, for the reason `focusOnRender`
 * requires: the heading only exists once the `@if` instantiates this component, so a
 * `viewChild.required` in `App` would resolve to nothing at `afterNextRender` time.
 * Constructed here, the render hook runs when the heading is real. Without it the
 * button the user just pressed is removed from the DOM and focus lands on `<body>` —
 * silence for a screen reader, and a tab order restarting from the top.
 *
 * Deliberately a full page rather than an overlay. A blocked navigation leaves it on
 * screen for MSAL's 30-second redirect timeout before the fallback runs, and a frozen
 * shell behind a spinner reads as a hang where an honest full-page state reads as work
 * in progress.
 *
 * It is not routed. A navigation is precisely what sign-out must not do: the guards are
 * latched off and MSAL is mid-redirect.
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
