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
import { ProjectDto } from '@domain/api/project';
import { PROJECTS_ROUTE } from '@core/auth/auth-routes';
import { provideComposerHost } from '@core/chat/composer-host';
import { provideComposerUploads } from '@core/documents/composer-uploads';
import { UploadStore } from '@core/documents/upload-store';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { Composer } from '@shared/composer/composer';
import { Icon } from '@shared/icon/icon';
import { MOBILE_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';
import { PillItem, PillSubnav } from '@shared/nav/pill-subnav/pill-subnav';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { ProjectComposerHost } from './project-composer-host';
import { ProjectConversationsStore } from '@core/projects/project-conversations-store';
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
    PillSubnav,
    MenuItem,
    MenuSeparator,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    Skeleton,
  ],
  // Two upload stores, because the two surfaces upload to different parents. The
  // project-bound one is provided here rather than on the files panel so switching tabs
  // mid-upload does not tear the transfer down; the composer gets its own, left unbound,
  // because a file attached there belongs to the conversation the prompt is about to
  // create — not to this project.
  providers: [
    UploadStore,
    provideComposerUploads(),
    ProjectComposerHost,
    provideComposerHost(ProjectComposerHost),
  ],
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

  /**
   * Below 768px the tab strip becomes the same scrollable pill strip the administration
   * area uses (frame `5m`'s treatment). Frame `4e`'s caption asks for stacked accordions
   * instead; that is a **deliberate departure**, agreed before implementation. These
   * three tabs are real child routes, one of them behind a `canDeactivate` guard that
   * can *refuse* the navigation — so a disclosure marked `aria-expanded` would announce
   * a state the control neither owns nor can guarantee. `aria-current="page"` on a link
   * is the honest statement, and reusing the strip means one responsive navigation
   * pattern in the application rather than two.
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
    // Here rather than in the files panel, because the count belongs to the tab strip
    // and has to be right while the reader is standing on Instructions.
    this._documents.bindProject(this.projectId);
    // Bound here for the same reason, and it is what makes the Conversations count
    // right while the reader is standing on another tab (US-908).
    this._conversations.bindProject(this.projectId);

    // Bound the moment the id is known, so a file dropped on the Files tab starts
    // uploading immediately. Only that store is bound: the composer's stays targetless,
    // which is what defers its files until a conversation exists to own them.
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
    // file dropped on the Files tab while the reader is on Instructions has to become a
    // row too, and the tab strip's own count would otherwise go stale. It reads the
    // project-bound store only — the composer's lives under `COMPOSER_UPLOADS` and is
    // never reached from here.
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

  /**
   * The star's name, which is its whole accessible name: the glyph has none of its own
   * and `aria-pressed` is deliberately absent, so the label has to carry the direction.
   */
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
