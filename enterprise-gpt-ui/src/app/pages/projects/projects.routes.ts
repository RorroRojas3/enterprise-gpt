import { Routes } from '@angular/router';

/**
 * No guard of its own: the tree already sits behind `authGuard` and `sessionGuard`, and projects
 * are owner-scoped server-side, so no permission applies.
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
        loadChildren: () => import('./project-detail.routes'),
      },
    ],
  },
];

export default routes;
