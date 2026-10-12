import { Routes } from '@angular/router';
import { UiKit } from './ui-kit';

/** Dev only: `app.routes.ts` guards it with `isDevMode()`, so production never loads the chunk. */
export const uiKitRoutes: Routes = [{ path: '', component: UiKit, title: 'UI kit' }];

export default uiKitRoutes;
