import { ChangeDetectionStrategy, Component, ElementRef, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { Icon } from '@shared/icon/icon';
import { focusOnRender } from '@shared/a11y/focus-on-render';
import { AuthPage } from '../auth-page/auth-page';

/**
 * Not `UnavailablePanel`: that has no action slot by design, so nothing it shows looks retryable,
 * and this page needs "Back to Chat".
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
