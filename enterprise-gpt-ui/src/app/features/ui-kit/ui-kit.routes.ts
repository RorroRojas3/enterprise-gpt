import { Routes } from '@angular/router';
import { UiKit } from './ui-kit';

/**
 * The kit gallery. Reached only in development — `app.routes.ts` guards it with
 * `canMatch: [() => isDevMode()]`, so the chunk is never requested in production.
 */
export const uiKitRoutes: Routes = [{ path: '', component: UiKit, title: 'UI kit' }];

export default uiKitRoutes;
