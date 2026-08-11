import { ChangeDetectionStrategy, Component, ElementRef, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { Icon } from '@shared/icon/icon';
import { focusOnRender } from '@shared/a11y/focus-on-render';
import { AuthPage } from '../auth-page/auth-page';

/**
 * Frame `6e` — signed in, but not an administrator.
 *
 * Reachable by URL only: `adminCanMatch` redirects here, and the shell renders no
 * Admin entry for a user without the grant, so nobody arrives by clicking.
 *
 * Not `UnavailablePanel`, though the shape is close. That component has no action
 * slot **by design** — its signature is what enforces "nothing here is retryable" —
 * and this frame needs exactly one "Back to Chat" action. Adding an action input
 * there to save a small component would weaken the guarantee for every screen that
 * uses it.
 */
@Component({
  selector: 'app-forbidden',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AuthPage, Icon, RouterLink],
  templateUrl: './forbidden.html',
  styleUrl: './forbidden.scss',
})
export class Forbidden {
  private readonly _heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  protected readonly chatRoute = CHAT_ROUTE;

  constructor() {
    // Arriving here removes the Admin link that had focus from the DOM entirely, which
    // drops focus onto <body> and leaves a keyboard user tabbing from the top.
    focusOnRender(this._heading);
  }
}
