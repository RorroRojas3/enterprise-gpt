import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Attachment } from '@shared/models/attachment.model';
import { TableColumn } from '@shared/models/table-column.model';
import { AppError } from '@core/errors/app-error';
import { ToastStore } from '@core/notifications/toast-store';
import { AttachmentChip } from '@shared/components/attachment-chip/attachment-chip';
import { AvatarInitials } from '@shared/components/avatar-initials/avatar-initials';
import { BrandIcon } from '@shared/components/brand-icon/brand-icon';
import { BrandLogo } from '@shared/components/brand-logo/brand-logo';
import { BulkActionBar } from '@shared/components/bulk-action-bar/bulk-action-bar';
import { CardRow } from '@shared/components/card-row/card-row';
import { DataTable } from '@shared/components/data-table/data-table';
import { DropOverlay } from '@shared/components/drop-overlay/drop-overlay';
import { EmptyState } from '@shared/components/empty-state/empty-state';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { Modal } from '@shared/components/modal/modal';
import { Offcanvas } from '@shared/components/offcanvas/offcanvas';
import { Paginator } from '@shared/components/paginator/paginator';
import { PillItem, PillSubnav } from '@shared/components/pill-subnav/pill-subnav';
import { Ridgeline } from '@shared/components/ridgeline/ridgeline';
import { SearchInput } from '@shared/components/search-input/search-input';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { SourceBadge } from '@shared/components/source-badge/source-badge';
import { StatusDot } from '@shared/components/status-dot/status-dot';
import { Switch } from '@shared/components/switch/switch';
import { ThemeToggle } from '@shared/components/theme-toggle/theme-toggle';
import { UnavailablePanel } from '@shared/components/unavailable-panel/unavailable-panel';
import { FileDropTarget } from '@shared/directives/file-drop-target';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';
import { MenuTriggerContent } from '@shared/directives/menu-trigger-content';
import { TableCell } from '@shared/directives/table-cell';
import { Tooltip } from '@shared/directives/tooltip';
import { PermissionBadge } from '@components/admin/permission-badge';
import { KindBadge } from '@components/chat/transcript/kind-badge';
import { ProjectCard } from '@components/projects/project-card';

interface DemoRow {
  readonly id: string;
  readonly name: string;
  readonly owner: string;
}

/** One set per pane, so each overlay opens inside, and in the theme of, the pane that asked. */
function paneOverlays() {
  return {
    modalSm: signal(false),
    modalMd: signal(false),
    offcanvasStart: signal(false),
    offcanvasEnd: signal(false),
  };
}

/**
 * Both themes side by side, for what unit tests cannot check: dark-theme legibility, the top-layer
 * overlays, and motion stopping under `prefers-reduced-motion`. Development only.
 */
@Component({
  selector: 'app-ui-kit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AttachmentChip,
    AvatarInitials,
    BrandIcon,
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
    MenuTriggerContent,
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
    Switch,
    TableCell,
    ThemeToggle,
    Tooltip,
    UnavailablePanel,
  ],
  host: { class: 'd-block px-4 pt-4 pb-5' },
  templateUrl: './ui-kit.html',
})
export class UiKit {
  protected readonly toasts = inject(ToastStore);

  protected readonly themes = ['light', 'dark'] as const;
  protected readonly overlays = { light: paneOverlays(), dark: paneOverlays() };

  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set<string>());
  protected readonly search = signal('');
  protected readonly page = signal(7);
  protected readonly pageSizes: readonly number[] = [25, 50, 100];
  protected readonly sizedPage = signal(1);
  protected readonly pageSize = signal(25);
  protected readonly pageCount = computed(() => Math.ceil(312 / this.pageSize()));

  protected readonly favourite = signal(true);
  protected readonly digest = signal(true);
  protected readonly ranges: readonly number[] = [7, 30, 90];
  protected readonly period = signal(30);
  protected readonly models: readonly string[] = [
    'GPT-5 Enterprise',
    'Claude Sonnet',
    'GPT-4.1 mini',
  ];
  protected readonly model = signal('GPT-5 Enterprise');
  protected readonly activePill = signal('users');
  protected readonly activeRow = signal('a');

  protected readonly switchOn = signal(true);
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

  protected resize(size: number): void {
    this.pageSize.set(size);
    this.sizedPage.set(1);
  }

  protected pickPill(event: Event, id: string): void {
    event.preventDefault();
    this.activePill.set(id);
  }

  protected pickRow(event: Event, id: string): void {
    event.preventDefault();
    this.activeRow.set(id);
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
