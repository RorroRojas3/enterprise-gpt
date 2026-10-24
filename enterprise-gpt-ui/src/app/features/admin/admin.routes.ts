import { Routes } from '@angular/router';
import { AdminLayout } from './admin-layout';
import { AdminModels } from './chat-models/admin-models';
import { AdminMcps } from './mcps/admin-mcps';
import { AdminReports } from './reports/admin-reports';
import { AdminUsers } from './users/admin-users';

/**
 * Reached only through `adminCanMatch`, so non-administrators never fetch it. Tabs are children
 * of one layout so the rail and the back button follow the router; each provides its own store,
 * which also makes the reports tab re-fetch on every entry.
 */
export const adminRoutes: Routes = [
  {
    path: '',
    component: AdminLayout,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'users' },
      { path: 'users', component: AdminUsers, title: 'Users — Enterprise GPT' },
      { path: 'models', component: AdminModels, title: 'Models — Enterprise GPT' },
      { path: 'mcps', component: AdminMcps, title: 'MCP servers — Enterprise GPT' },
      { path: 'reports', component: AdminReports, title: 'Reports — Enterprise GPT' },

      // Last, and inside the area: otherwise an unmatched segment backtracks to the app's `**`
      // and lands on `/chat`. It also consumes every `/admin` URL, so new routes go above this
      // entry, or above the layout route to render without the rail.
      { path: '**', redirectTo: 'users' },
    ],
  },
];

export default adminRoutes;
