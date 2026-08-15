import { Routes } from '@angular/router';

/**
 * The projects area: the grid (frame `4c`) and one project's detail screen
 * (frames `4e`–`4g`).
 *
 * `loadChildren` rather than two sibling `loadComponent`s because both screens invoke
 * the same create/rename/delete dialogs, and a shared parent is what lets those mount
 * **once** — `ProjectsLayout` is to this area what `Shell` is to the signed-in app. No
 * guard of its own: the whole tree already sits behind `authGuard` and `sessionGuard`,
 * and projects are scoped to their owner server-side, so there is nothing here a
 * permission could gate.
 */
const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./projects-layout').then((m) => m.ProjectsLayout),
    children: [
      {
        path: '',
        loadComponent: () => import('./projects').then((m) => m.Projects),
        title: 'Projects — Enterprise GPT',
      },
      {
        path: ':projectId',
        loadChildren: () => import('./detail/project-detail.routes'),
      },
    ],
  },
];

export default routes;
