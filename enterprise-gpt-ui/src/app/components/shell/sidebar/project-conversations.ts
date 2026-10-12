import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { canRetry } from '@core/errors/error-message';
import { ProjectConversationsStore } from '@core/projects/project-conversations-store';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Icon } from '@shared/icon/icon';

const SKELETON_ROWS = 2;

/**
 * Provides its own store, created on expand and destroyed on collapse, so each open node has
 * its own query and re-opening re-reads. The store is in `core/` as the project screen shares it.
 */
@Component({
  selector: 'app-sidebar-project-conversations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ErrorPanel, Icon, RouterLink, RouterLinkActive, Skeleton],
  providers: [ProjectConversationsStore],
  templateUrl: './project-conversations.html',
  styleUrl: './project-conversations.scss',
})
export class SidebarProjectConversations {
  protected readonly conversations = inject(ProjectConversationsStore);

  readonly projectId = input.required<string>();

  /** Names the paging control; the list itself is named by the row above. */
  readonly projectName = input.required<string>();

  protected readonly chatRoute = CHAT_ROUTE;
  protected readonly skeletonRows = Array.from({ length: SKELETON_ROWS });

  /** A 403 answers the same way however often it is asked. */
  protected readonly canRetry = canRetry;

  /** Exact, so `/chat/a` does not also light up while `/chat/b` is open. */
  protected readonly exact = { exact: true } as const;

  constructor() {
    // The signal itself, so a changed project id re-reads and cancels the old request.
    this.conversations.bindProject(this.projectId);
  }
}
