import { Routes } from '@angular/router';
import { AdminLayout } from './admin-layout';
import { AdminMcps } from './mcps/admin-mcps';
import { AdminModels } from './models/admin-models';
import { AdminReports } from './reports/admin-reports';
import { AdminUsers } from './users/admin-users';

/**
 * The administration chunk.
 *
 * Reached only through `adminCanMatch` on the route that carries `loadChildren`, so
 * this file is never fetched by a browser whose user lacks the Administrator
 * permission. `scripts/check-initial-chunk.mjs` keeps it out of the initial graph.
 *
 * Tabs are **children of one layout route**, which is what lets the rail mark the open
 * one from the router rather than from local state, and what makes the browser's back
 * button restore the previous tab for free (US-1209). All four the board draws are here;
 * US-1302 changed what `reports` renders, not that it resolves — the route, its title and its
 * rail entry are unchanged.
 *
 * Each tab provides its own store on the component the route names, never on the layout —
 * US-1209 requires that opening one tab instantiates that tab's store and no other. That
 * placement is also what makes the reports tab re-fetch on every entry (FR-41): a component-level
 * provider is rebuilt each time the route is entered, so there is nothing left alive to serve a
 * cached report.
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

      // Last, and inside the area rather than outside it. Without this an unmatched
      // segment makes the router backtrack out of `/admin` entirely and fall to the
      // table's own `**`, which lands an administrator on `/chat` — a mistyped or
      // renamed tab URL ejecting them from the area is the opposite of what a story
      // about reaching a tab by URL is for. The redirect is relative, so it resolves
      // against this parent to `/admin/users` during recognition, with no layout flash.
      //
      // It also ends the backtracking a *sibling* of the layout route would rely on:
      // the layout is `path: ''` and matches by prefix, so from here on every URL under
      // `/admin` is consumed here. A new admin route therefore belongs in this array
      // above this entry, or above the layout route itself if it is to render without
      // the rail — added below either, it would be unreachable and nothing would say so.
      { path: '**', redirectTo: 'users' },
    ],
  },
];

export default adminRoutes;
