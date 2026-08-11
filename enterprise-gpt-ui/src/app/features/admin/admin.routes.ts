import { Routes } from '@angular/router';
import { AdminUsers } from './admin-users/admin-users';

/**
 * The administration chunk.
 *
 * Reached only through `adminCanMatch` on the route that carries `loadChildren`, so
 * this file is never fetched by a browser whose user lacks the Administrator
 * permission. `scripts/check-initial-chunk.mjs` keeps it out of the initial graph.
 *
 * US-1207 turns `users` into one of four deep-linkable sibling tabs, each lazy with
 * its own route-scoped store.
 */
export const adminRoutes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'users' },
  { path: 'users', component: AdminUsers, title: 'Users — Enterprise GPT' },
];

export default adminRoutes;
