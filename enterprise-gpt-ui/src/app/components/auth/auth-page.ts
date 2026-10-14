import { ChangeDetectionStrategy, Component } from '@angular/core';

/** The chrome-free frame every full-page auth state sits in. Layout only; states own their copy. */
@Component({
  selector: 'app-auth-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'd-flex align-items-center justify-content-center min-dvh-100 p-4 bg-body' },
  template: `
    <main class="d-flex flex-column align-items-center gap-3 mw-sm text-center">
      <ng-content />
    </main>
  `,
})
export class AuthPage {}
