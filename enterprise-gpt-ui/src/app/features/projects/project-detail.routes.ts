import { Routes } from '@angular/router';
import { ProjectConversationsStore } from '@core/projects/project-conversations-store';
import { ProjectDocumentsStore } from './project-documents-store';
import { ProjectStore } from './project-store';
import { unsavedInstructionsGuard } from './unsaved-instructions-guard';

/**
 * Tabs are real routes, not a local index, so the open panel survives a refresh and can be linked,
 * and `canDeactivate` can guard unsaved instructions on a tab switch.
 */
const routes: Routes = [
  {
    path: '',
    // On the route, not the panels: the parent and every tab share one instance, and the tab
    // strip's counts must be readable from Instructions, where neither panel exists.
    providers: [ProjectStore, ProjectDocumentsStore, ProjectConversationsStore],
    loadComponent: () => import('./project-detail').then((m) => m.ProjectDetail),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'instructions' },
      {
        path: 'instructions',
        canDeactivate: [unsavedInstructionsGuard],
        loadComponent: () => import('./instructions-panel').then((m) => m.InstructionsPanel),
      },
      {
        path: 'files',
        loadComponent: () => import('./project-files').then((m) => m.ProjectFiles),
      },
      {
        path: 'conversations',
        loadComponent: () => import('./project-conversations').then((m) => m.ProjectConversations),
      },
    ],
  },
];

export default routes;
