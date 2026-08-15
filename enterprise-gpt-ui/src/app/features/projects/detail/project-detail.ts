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
import { PROJECTS_ROUTE } from '@core/auth/auth-routes';
import { provideComposerHost } from '@core/chat/composer-host';
import { UploadStore } from '@core/documents/upload-store';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { Composer } from '@shared/composer/composer';
import { Icon } from '@shared/icon/icon';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { ProjectComposerHost } from './project-composer-host';
import { ProjectConversationsStore } from './conversations/project-conversations-store';
import { ProjectDocumentsStore } from './files/project-documents-store';
import { ProjectStore } from './project-store';

/**
 * One project's screen — the header, the composer slot and the tab strip (frame `4e`).
 *
 * The board's order is load-bearing: header, then composer, then tabs. Sending from the
 * composer creates a conversation *inside* this project (US-906), which is why it sits
 * above the panels rather than inside one — it belongs to the project, not to whichever
 * tab happens to be open.
 *
 * Frame `4e` draws three tabs and a favourite star. The **Conversations** tab is
 * US-908's and waits on US-307 for its row menu; the star waits on US-909 for a flag to
 * put on the wire. Both are absent rather than disabled, the repo's pattern for an
 * unshipped affordance.
 */
@Component({
  selector: 'app-project-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    Composer,
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    Skeleton,
  ],
  // `UploadStore` is provided here rather than on the files panel, so a file dropped
  // on the composer and a file dropped on the Files tab are the same set of chips — and
  // so switching tabs mid-upload does not tear the transfer down.
  providers: [UploadStore, ProjectComposerHost, provideComposerHost(ProjectComposerHost)],
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

  /**
   * Frame `4e`'s count beside the Files tab.
   *
   * Null until the list has actually been read: "Files 0" on a project that has four is
   * a worse answer than no count, and the strip renders before the panel it counts.
   */
  protected readonly filesCount = computed(() =>
    this._documents.isFulfilled() ? this._documents.count() : null,
  );

  /** Frame `4e`'s count beside the Conversations tab (US-908), withheld the same way. */
  protected readonly conversationsCount = computed(() => this._conversations.count());

  constructor() {
    // A computation, not a value: navigating from one project to another reuses this
    // component, and `switchMap` inside each store cancels the request left behind.
    this.project.bindRoute(this.projectId);
    // Here rather than in the files panel, because the count belongs to the tab strip
    // and has to be right while the reader is standing on Instructions.
    this._documents.bindProject(this.projectId);
    // Bound here for the same reason, and it is what makes the Conversations count
    // right while the reader is standing on another tab (US-908).
    this._conversations.bindProject(this.projectId);

    // Bound the moment the id is known. Unlike the chat composer's, this target is
    // never absent — the screen only renders under a resolved `:projectId` — so a
    // dropped file starts uploading immediately rather than waiting for a conversation
    // to exist. Files added from either surface belong to the project.
    effect(() => {
      const projectId = this.projectId();
      untracked(() => this._uploads.bindProject(projectId));
    });

    // A chip that finished is a row nobody has read yet: the upload route answers with
    // a job id, never a document, so the name, size and created date can only come from
    // asking for the list again — after which the chip retires, or the same file would
    // sit on screen twice.
    //
    // Here rather than on the files panel because `UploadStore` is provided here: a
    // file dropped on the composer while the reader is on Instructions has to become a
    // row too, and the tab strip's own count would otherwise go stale.
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

    // Deleted from this screen's own kebab, or from another tab. The store cannot
    // navigate, so it raises a flag and the screen leaves — to the grid, because the
    // project this URL names no longer exists and reloading would 404.
    effect(() => {
      if (this.project.deleted()) {
        untracked(() => void this._router.navigate([PROJECTS_ROUTE]));
      }
    });
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
