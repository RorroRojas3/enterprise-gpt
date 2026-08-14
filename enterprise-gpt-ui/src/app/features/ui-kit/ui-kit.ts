import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ToastStore } from '@core/notifications/toast-store';
import { AppError } from '@core/errors/app-error';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { KindBadge } from '@shared/badge/kind-badge/kind-badge';
import { PermissionBadge } from '@shared/badge/permission-badge/permission-badge';
import { SourceBadge } from '@shared/badge/source-badge/source-badge';
import { StatusDot } from '@shared/badge/status-dot/status-dot';
import { ProjectCard } from '@shared/card/project-card/project-card';
import { AttachmentChip } from '@shared/chip/attachment-chip/attachment-chip';
import { Attachment } from '@shared/chip/attachment-chip/attachment.model';
import { BulkActionBar } from '@shared/data/bulk-action-bar/bulk-action-bar';
import { CardRow } from '@shared/data/card-row/card-row';
import { DataTable } from '@shared/data/data-table/data-table';
import { TableCell } from '@shared/data/data-table/table-cell';
import { TableColumn } from '@shared/data/data-table/table-column';
import { Paginator } from '@shared/data/paginator/paginator';
import { EmptyState } from '@shared/feedback/empty-state/empty-state';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { UnavailablePanel } from '@shared/feedback/unavailable-panel/unavailable-panel';
import { SearchInput } from '@shared/form/search-input/search-input';
import { Icon } from '@shared/icon/icon';
import { PillItem, PillSubnav } from '@shared/nav/pill-subnav/pill-subnav';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { Modal } from '@shared/overlay/modal/modal';
import { Offcanvas } from '@shared/overlay/offcanvas/offcanvas';
import { Tooltip } from '@shared/overlay/tooltip/tooltip';
import { Ridgeline } from '@shared/ridgeline/ridgeline';
import { DropOverlay } from '@shared/upload/drop-overlay';
import { FileDropTarget } from '@shared/upload/file-drop-target';

interface DemoRow {
  readonly id: string;
  readonly name: string;
  readonly owner: string;
}

/**
 * Every kit component, in both themes side by side.
 *
 * It exists because the kit has no consumer until EP-3, and two of its properties
 * cannot be checked by a unit test: that a component reads correctly in dark as well
 * as light, and that motion stops under `prefers-reduced-motion`. `data-bs-theme` is
 * per-subtree, so a split view shows both at once.
 *
 * Development only. Delete it once the real screens exercise the kit.
 */
@Component({
  selector: 'app-ui-kit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AttachmentChip,
    BrandLogo,
    BulkActionBar,
    CardRow,
    DataTable,
    DropOverlay,
    EmptyState,
    ErrorPanel,
    FileDropTarget,
    Icon,
    KindBadge,
    Menu,
    MenuItem,
    MenuSeparator,
    Modal,
    Offcanvas,
    Paginator,
    PermissionBadge,
    PillSubnav,
    ProjectCard,
    Ridgeline,
    SearchInput,
    Skeleton,
    SourceBadge,
    StatusDot,
    TableCell,
    Tooltip,
    UnavailablePanel,
  ],
  templateUrl: './ui-kit.html',
  styleUrl: './ui-kit.scss',
})
export class UiKit {
  protected readonly toasts = inject(ToastStore);

  protected readonly modalOpen = signal(false);
  protected readonly offcanvasOpen = signal(false);
  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set<string>());
  protected readonly search = signal('');
  protected readonly page = signal(7);

  /** Stands in for `SessionStore.canUploadFiles()`, which no dev session can toggle. */
  protected readonly canUpload = signal(true);
  protected readonly lastDrop = signal('Nothing dropped yet.');

  protected noteDrop(files: readonly File[]): void {
    this.lastDrop.set(`Dropped: ${files.map((file) => file.name).join(', ')}`);
  }

  protected readonly rows: readonly DemoRow[] = [
    { id: 'a', name: 'Helios release status', owner: 'Sofía Herrera' },
    { id: 'b', name: 'Q3 revenue model', owner: 'Marco Díaz' },
    { id: 'c', name: 'Incident 4821 postmortem', owner: 'Ada Okafor' },
  ];

  protected readonly columns: readonly TableColumn<DemoRow>[] = [
    { key: 'name', header: 'Name', width: 'minmax(0, 1.4fr)', mobile: 'title' },
    {
      key: 'owner',
      header: 'Owner',
      width: '160px',
      text: (row) => row.owner,
      mobile: 'subtitle',
    },
    {
      key: 'actions',
      header: 'Actions',
      width: '44px',
      align: 'end',
      headerHidden: true,
      mobile: 'actions',
    },
  ];

  protected readonly trackKey = (row: DemoRow): string => row.id;
  protected readonly rowLabel = (row: DemoRow): string => row.name;

  protected readonly pendingIds: ReadonlySet<string> = new Set(['b']);

  protected readonly attachments: readonly Attachment[] = [
    {
      id: '1',
      fileName: 'q3-report.pdf',
      sizeLabel: '2.4 MB',
      state: { kind: 'uploading', percent: 42 },
    },
    {
      id: '2',
      fileName: 'forecast.xlsx',
      sizeLabel: '840 KB',
      state: { kind: 'processing', subStatus: 'Generating embeddings… 3 of 12 pages' },
    },
    { id: '3', fileName: 'notes.md', sizeLabel: '12 KB', state: { kind: 'ready' } },
    {
      id: '4',
      fileName: 'scanned.pdf',
      sizeLabel: '8.2 MB',
      // A job the pipeline could not finish, carrying the server's sanitized message.
      // Only this arm offers Retry — the same file can succeed on a second attempt.
      state: {
        kind: 'failed',
        reason: 'No readable text was found in the document, so there is nothing to index.',
      },
    },
    {
      id: '5',
      fileName: 'demo.mov',
      sizeLabel: '212 MB',
      // Refused before any request. The supported set is built from the API's own
      // `file-extensions` list in the app; here it is written out for the demo.
      state: {
        kind: 'unsupported',
        reason: '.mov isn’t supported — DOC, DOCX, MD, PDF, PPTX, TXT',
      },
    },
    { id: '6', fileName: 'archive.har', sizeLabel: null, state: { kind: 'unknown' } },
  ];

  protected readonly pills: readonly PillItem[] = [
    { id: 'users', label: 'Users', link: '/ui-kit' },
    { id: 'models', label: 'Models', link: '/ui-kit' },
    { id: 'mcp', label: 'MCP servers', link: '/ui-kit' },
    { id: 'reports', label: 'Reports', link: '/ui-kit' },
  ];

  protected clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  protected readonly demoError: AppError = {
    kind: 'resource-not-found',
    status: 404,
    title: 'Not Found',
    detail: null,
    traceId: '8f42-a1c9-77d0-3b61',
    instance: null,
  } as AppError;

  /** A retry handler is what makes the toast's Retry render at all — see `ToastStore`. */
  protected readonly demoRetry = (): void => {
    this.toasts.info('Retried');
  };
}
