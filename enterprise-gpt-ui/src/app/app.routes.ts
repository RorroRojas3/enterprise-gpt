import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    // The kit gallery. `canMatch` rather than `canActivate`, so the chunk is never
    // even requested in production — the same mechanism US-203 uses to keep the admin
    // chunk off a non-administrator's device.
    path: 'ui-kit',
    canMatch: [() => isDevMode()],
    loadChildren: () => import('@features/ui-kit/ui-kit.routes'),
  },
];
