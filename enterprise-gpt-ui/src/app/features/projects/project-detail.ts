import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ProjectDto } from '@shared/models/api/project.model';
import { MOBILE_VIEWPORT } from '@shared/utils/layout/breakpoints';
import { provideComposerHost } from '@core/chat/composer-host.token';
import { PROJECTS_ROUTE } from '@core/navigation/route-paths';
import { injectMediaQuery } from '@core/ui/media-query';
import { provideComposerUploads } from '@state/documents/composer-uploads.token';
import { UploadStore } from '@state/documents/upload-store';
import { ProjectActionsStore } from '@state/project-actions-store';
import { ProjectComposerHost } from '@state/projects/project-composer-host-store';
import { ProjectConversationsStore } from '@state/projects/project-conversations-store';
import { ProjectDocumentsStore } from '@state/projects/project-documents-store';
import { ProjectStore } from '@state/projects/project-store';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { PillItem, PillSubnav } from '@shared/components/pill-subnav/pill-subnav';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';
import { Composer } from '@components/composer/composer';

/**
 * The composer sits above the tabs, not inside one: a prompt sent here creates a conversation in
 * the project, whichever tab is open.
 */
@Component({
  selector: 'app-project-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    Composer,
    Icon,
    Menu,
    PillSubnav,
    MenuItem,
    MenuSeparator,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    Skeleton,
  ],
  // Two upload stores: the project's lives here so a tab switch does not cancel a transfer, and
  // the composer's stays unbound because its files belong to the conversation it will create.
  providers: [
    UploadStore,
    provideComposerUploads(),
    ProjectComposerHost,
    provideComposerHost(ProjectComposerHost),
  ],
  // The shell's `<main>` clips its overflow, so a routed screen scrolls itself.
  host: { class: 'd-flex flex-column gap-3 p-3 p-md-4 overflow-y-auto' },
  templateUrl: './project-detail.html',
  styleUrl: './project-detail.scss',
})
export class ProjectDetail {
  /** Bound from `:projectId` by `withComponentInputBinding()`. */
  readonly projectId = input.required<string>();

  protected readonly project = inject(ProjectStore);
  protected readonly actions = inject(ProjectActionsStore);

  private readonly _documents = inject(ProjectDocumentsStore);
  private readonly _conversations = inject(ProjectConversationsStore);
  private readonly _uploads = inject(UploadStore);
  private readonly _router = inject(Router);

  /** Null until the list is read: "Files 0" on a project that has four is worse than no count. */
  protected readonly filesCount = computed(() =>
    this._documents.isFulfilled() ? this._documents.count() : null,
  );

  /** Withheld until read, like {@link filesCount}. */
  protected readonly conversationsCount = computed(() => this._conversations.count());

  /**
   * Narrow screens get the pill strip, not accordions: the tabs are child routes a guard can
   * refuse, so `aria-expanded` would claim a state the control cannot guarantee.
   */
  protected readonly isNarrow = injectMediaQuery(MOBILE_VIEWPORT);

  protected readonly pills = computed<readonly PillItem[]>(() => [
    { id: 'instructions', label: 'Instructions', link: 'instructions' },
    { id: 'files', label: 'Files', link: 'files', count: this.filesCount() },
    {
      id: 'conversations',
      label: 'Conversations',
      link: 'conversations',
      count: this.conversationsCount(),
    },
  ]);

  constructor() {
    // A computation, not a value: navigating from one project to another reuses this
    // component, and `switchMap` inside each store cancels the request left behind.
    this.project.bindRoute(this.projectId);
    // Bound here, not in the panels, so the tab strip's counts are right from any tab.
    this._documents.bindProject(this.projectId);
    this._conversations.bindProject(this.projectId);

    // Only the project's store is bound; the composer's stays targetless until a conversation
    // exists to own its files.
    effect(() => {
      const projectId = this.projectId();
      untracked(() => this._uploads.bindProject(projectId));
    });

    // The upload route answers with a job id, so a ready chip becomes a row only by re-reading
    // the list; the chip then retires so the file is not shown twice.
    effect(() => {
      const settled = this._uploads.attachments().filter((row) => row.state.kind === 'ready');
      if (settled.length === 0) {
        return;
      }

      untracked(() => {
        for (const row of settled) {
          this._uploads.remove(row.id);
        }
        this._documents.refresh();
      });
    });

    // Deleted here or in another tab: the store cannot navigate, so the screen leaves for the grid.
    effect(() => {
      if (this.project.deleted()) {
        untracked(() => void this._router.navigate([PROJECTS_ROUTE]));
      }
    });
  }

  /** The star's whole accessible name; with no `aria-pressed`, it carries the direction. */
  protected favoriteLabel(project: ProjectDto): string {
    return project.isFavorite ? `Unfavourite ${project.name}` : `Favourite ${project.name}`;
  }

  protected beginEdit(): void {
    const project = this.project.project();
    if (project !== null) {
      this.actions.beginEdit(project);
    }
  }

  protected beginDelete(): void {
    const project = this.project.project();
    if (project !== null) {
      this.actions.beginDelete(project);
    }
  }
}
