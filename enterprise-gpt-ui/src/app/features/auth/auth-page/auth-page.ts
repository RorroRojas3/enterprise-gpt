import { ChangeDetectionStrategy, Component } from '@angular/core';

/** The chrome-free frame every full-page auth state sits in. Layout only; states own their copy. */
@Component({
  selector: 'app-auth-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="auth-page">
      <div class="auth-page__content"><ng-content /></div>
    </main>
  `,
  styleUrl: './auth-page.scss',
})
export class AuthPage {}
