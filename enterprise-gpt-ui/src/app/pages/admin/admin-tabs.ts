import { ADMIN_ROUTE } from '@core/auth/auth-routes';
import { IconName } from '@shared/icon/icon-names';
import { PillItem } from '@shared/nav/pill-subnav/pill-subnav';

/** One entry of the administration rail, or of its pill strip below 768px. */
export interface AdminTab {
  readonly id: string;
  readonly label: string;
  readonly icon: IconName;
  readonly link: string;
}

/**
 * An entry lands with its route, never ahead of it: a rail entry leading nowhere is the "shown
 * and disabled" affordance already rejected for the Admin entry itself.
 */
export const ADMIN_TABS: readonly AdminTab[] = [
  { id: 'users', label: 'Users', icon: 'bi-people', link: `${ADMIN_ROUTE}/users` },
  { id: 'models', label: 'Models', icon: 'bi-cpu', link: `${ADMIN_ROUTE}/models` },
  { id: 'mcps', label: 'MCP servers', icon: 'bi-plug', link: `${ADMIN_ROUTE}/mcps` },
  { id: 'reports', label: 'Reports', icon: 'bi-graph-up', link: `${ADMIN_ROUTE}/reports` },
];

/** The same tabs as {@link PillSubnav} takes them, below 768px. */
export const ADMIN_PILLS: readonly PillItem[] = ADMIN_TABS.map(({ id, label, link }) => ({
  id,
  label,
  link,
}));
