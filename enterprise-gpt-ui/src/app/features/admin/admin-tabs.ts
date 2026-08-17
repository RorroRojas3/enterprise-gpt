import { ADMIN_ROUTE } from '@core/auth/auth-routes';
import { IconName } from '@shared/icon/icon-names';
import { PillItem } from '@shared/nav/pill-subnav/pill-subnav';

/** One entry of the administration rail — frame `5a`'s 190px nav, frame `5m`'s pills. */
export interface AdminTab {
  readonly id: string;
  readonly label: string;
  readonly icon: IconName;
  readonly link: string;
}

/**
 * The tabs the administration area offers, in the board's own order.
 *
 * `AdminNav.dc.html` draws four — Users, Models, MCP servers, Reports — and this list
 * holds only the ones that exist. A tab is added by the story that builds its route
 * (US-1207, US-1208, US-1302), never ahead of it: a rail entry leading nowhere is the
 * "shown and disabled" affordance US-203 already rejected for the Admin entry itself.
 */
export const ADMIN_TABS: readonly AdminTab[] = [
  { id: 'users', label: 'Users', icon: 'bi-people', link: `${ADMIN_ROUTE}/users` },
];

/** The same tabs as {@link PillSubnav} takes them, below 768px (frame `5m`). */
export const ADMIN_PILLS: readonly PillItem[] = ADMIN_TABS.map(({ id, label, link }) => ({
  id,
  label,
  link,
}));
