import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CHAT_ROUTE } from '@core/auth/auth-routes';

/**
 * Placeholder for the admin users tab.
 *
 * **EP-12 replaces this** — US-1201 through US-1204 bring the paginated directory,
 * the model catalog, the MCP catalog and the deep-linkable tabs. It exists now
 * because US-203's criteria are about a *chunk*: there has to be something real
 * behind `loadChildren` for `adminCanMatch` to withhold, and an administrator has to
 * land on a users tab that renders.
 */
@Component({
  selector: 'app-admin-users',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <main class="admin-users">
      <h1 class="admin-users__heading font-display">Users</h1>
      <p class="admin-users__note">
        The administration area arrives with EP-12. You are seeing this because you hold the
        Administrator permission.
      </p>
      <a class="btn btn-outline-secondary btn-sm" [routerLink]="chatRoute">Back to Chat</a>
    </main>
  `,
  styles: `
    :host {
      display: block;
    }

    .admin-users {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 12px;
      max-width: 720px;
      margin: 0 auto;
      padding: 48px 24px;
    }

    .admin-users__heading {
      margin: 0;
      font-size: 20px;
      font-weight: 600;
    }

    .admin-users__note {
      margin: 0;
      color: var(--muted);
      font-size: 13.5px;
      line-height: 1.55;
    }
  `,
})
export class AdminUsers {
  protected readonly chatRoute = CHAT_ROUTE;
}
