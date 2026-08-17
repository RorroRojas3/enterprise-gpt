import { Routes } from '@angular/router';
import { AdminLayout } from './admin-layout';
import { AdminModels } from './models/admin-models';
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
 * button restore the previous tab for free. US-1208 and US-1302 each add a sibling here;
 * US-1209 owns the criteria across all four.
 *
 * Each tab provides its own store on its own route, never on the layout — US-1209
 * requires that opening one tab instantiates that tab's store and no other.
 */
export const adminRoutes: Routes = [
  {
    path: '',
    component: AdminLayout,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'users' },
      { path: 'users', component: AdminUsers, title: 'Users — Enterprise GPT' },
      { path: 'models', component: AdminModels, title: 'Models — Enterprise GPT' },
    ],
  },
];

export default adminRoutes;
