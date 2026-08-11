import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The centred, chrome-free frame every full-page auth state sits in — frames `6a`,
 * `6b`, `6c`, `6e` and their dark counterpart `6f`.
 *
 * It carries the layout only, so each state owns its own copy and controls. The
 * theme needs no work here: `index.html`'s pre-paint script has already set
 * `data-bs-theme`, which is why frame `6f` is the same component as `6a`.
 */
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
