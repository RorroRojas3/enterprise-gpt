import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ADMIN_ROUTE } from '@core/auth/auth-routes';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { SessionStore } from '@core/session/session-store';
import { BrandLogo } from '@shared/brand-logo/brand-logo';

/**
 * Placeholder for the chat surface.
 *
 * **EP-3 and EP-4 replace this entirely** — the sidebar (US-301, US-302), the empty
 * state of frame `1a`, and the composer all land there. It exists now so EP-2's
 * guards have somewhere to succeed to, and it renders the session and catalog state
 * so a live sign-in can be verified end to end without a browser devtools session.
 *
 * The Admin link is gated on the permission rather than shown-and-disabled: US-203
 * requires the entry to be **absent** for a non-administrator, so `@if` is the
 * criterion, not a styling choice. It moves to the sidebar in US-301.
 */
@Component({
  selector: 'app-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLogo, RouterLink],
  templateUrl: './chat.html',
  styleUrl: './chat.scss',
})
export class Chat {
  protected readonly adminRoute = ADMIN_ROUTE;
  protected readonly session = inject(SessionStore);
  protected readonly models = inject(ModelCatalogStore);
  protected readonly mcps = inject(McpCatalogStore);
}
