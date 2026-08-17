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
 * All four `AdminNav.dc.html` draws, since US-1209 gave the last of them a route. The
 * rule that got them here is unchanged and still binds: **an entry is added by the story
 * that builds its route, in the same commit, never ahead of it** — a rail entry leading
 * nowhere is the "shown and disabled" affordance US-203 already rejected for the Admin
 * entry itself. Reports leads to frame `5i`'s unavailable panel rather than to a
 * dashboard, which is a destination; US-1302 changes what is on it, not whether it is
 * reachable.
 */
export const ADMIN_TABS: readonly AdminTab[] = [
  { id: 'users', label: 'Users', icon: 'bi-people', link: `${ADMIN_ROUTE}/users` },
  { id: 'models', label: 'Models', icon: 'bi-cpu', link: `${ADMIN_ROUTE}/models` },
  // `bi-plug`, which is what `AdminNav.dc.html` draws and is already in the sprite.
  { id: 'mcps', label: 'MCP servers', icon: 'bi-plug', link: `${ADMIN_ROUTE}/mcps` },
  // `bi-graph-up`, likewise drawn by the board and already among the sprite's glyphs.
  { id: 'reports', label: 'Reports', icon: 'bi-graph-up', link: `${ADMIN_ROUTE}/reports` },
];

/** The same tabs as {@link PillSubnav} takes them, below 768px (frame `5m`). */
export const ADMIN_PILLS: readonly PillItem[] = ADMIN_TABS.map(({ id, label, link }) => ({
  id,
  label,
  link,
}));
