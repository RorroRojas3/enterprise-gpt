import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { CHAT_ROUTE } from '@core/auth/auth-routes';
import { canRetry } from '@core/errors/error-message';
import { ProjectConversationsStore } from '@core/projects/project-conversations-store';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Icon } from '@shared/icon/icon';

/** How many skeleton rows stand in for a node's list while its first page loads. */
const SKELETON_ROWS = 2;

/**
 * The conversations of one expanded favourite project, nested under its row (US-910).
 *
 * **It provides its own {@link ProjectConversationsStore}**, which is the whole reason it
 * is a component rather than markup inside `SidebarProjectRow`: the sidebar can have
 * several nodes open at once, each needing its own `projectId`-filtered query, and a
 * store on the row would exist for every favourite whether or not it was ever opened.
 * Mounted behind the row's `@if`, the store is created when a node expands and destroyed
 * when it collapses, so re-opening a node re-reads rather than showing a set that went
 * stale while it was shut.
 *
 * It reads US-907's filter — one request, no drain — through the same store the project
 * detail screen's Conversations tab uses (US-908), which is why that store lives in
 * `core/`: `features/shell` may not import `features/projects`.
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

  /**
   * Names the paging control, which is the one place in this panel that has to say
   * *which* project it is loading more of. The list itself is named by the row above it.
   */
  readonly projectName = input.required<string>();

  protected readonly chatRoute = CHAT_ROUTE;
  protected readonly skeletonRows = Array.from({ length: SKELETON_ROWS });

  /** A 403 answers the same way however often it is asked. */
  protected readonly canRetry = canRetry;

  /** Exact, so `/chat/a` does not also light up while `/chat/b` is open. */
  protected readonly exact = { exact: true } as const;

  constructor() {
    // The signal itself, in an injection context: a node whose project id changed under
    // it re-reads, and `switchMap` cancels what it left behind.
    this.conversations.bindProject(this.projectId);
  }
}
