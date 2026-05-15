import { Routes } from '@angular/router';
import { HomeComponent } from './pages/home/home.component';
import { MsalGuard } from '@azure/msal-angular';
import { ConversationsComponent } from './pages/conversations/conversations.component';

export const routes: Routes = [
  {
    path: '',
    redirectTo: '/conversation',
    pathMatch: 'full',
  },
  {
    path: 'conversation',
    component: HomeComponent,
    canActivate: [MsalGuard],
  },
  {
    path: 'conversation/:conversationId',
    component: HomeComponent,
    canActivate: [MsalGuard],
  },
  {
    path: 'conversations',
    component: ConversationsComponent,
    canActivate: [MsalGuard],
  },
  {
    path: '**',
    redirectTo: '/conversation',
  },
];
