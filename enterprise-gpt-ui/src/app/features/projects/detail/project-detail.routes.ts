import { Routes } from '@angular/router';
import { ProjectDocumentsStore } from './files/project-documents-store';
import { ProjectStore } from './project-store';
import { unsavedInstructionsGuard } from './instructions/unsaved-instructions.guard';

/**
 * One project's screen: the header and composer, then a tab per panel (frames
 * `4e`–`4g`).
 *
 * The tabs are **real routes**, not a local index. Two things follow from that and
 * neither is available to a signal holding a tab name: the panel a reader is on
 * survives a refresh and can be linked to, and `canDeactivate` has something to attach
 * to — which is how US-904's unsaved-instructions warning fires on a move to the Files
 * tab as well as on a move out of the project.
 *
 * `ProjectStore` is provided on the route rather than on the component so the parent
 * and every tab share one instance, and so it is torn down when the reader leaves the
 * project rather than when one panel unmounts.
 */
const routes: Routes = [
  {
    path: '',
    // Both stores on the route rather than on their panels: the parent and every tab
    // share one instance of each, and the file count frame `4e` puts on the tab strip
    // has to be readable from the Instructions tab, where the files panel does not
    // exist.
    providers: [ProjectStore, ProjectDocumentsStore],
    loadComponent: () => import('./project-detail').then((m) => m.ProjectDetail),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'instructions' },
      {
        path: 'instructions',
        canDeactivate: [unsavedInstructionsGuard],
        loadComponent: () =>
          import('./instructions/instructions-panel').then((m) => m.InstructionsPanel),
      },
      {
        path: 'files',
        loadComponent: () => import('./files/project-files').then((m) => m.ProjectFiles),
      },
    ],
  },
];

export default routes;
