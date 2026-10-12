import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import {
  EXPORT_FORMATS,
  EXPORT_FORMAT_LABELS,
  ExportFormat,
} from '@shared/models/api/conversation-export.model';
import { IconName } from '@shared/utils/icons/icon-names';
import { ConversationExportStore } from '@state/conversation-export-store';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { MenuItem } from '@shared/directives/menu-item';

let nextId = 0;

interface DownloadOption {
  readonly format: ExportFormat;
  readonly label: string;
  readonly icon: IconName;
}

/**
 * In `shared/` so the composer and the conversation header offer one menu. It stays open while a
 * format prepares and closes only once the download starts, so after a failure another format
 * is one click away.
 */
@Component({
  selector: 'app-conversation-download-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Menu, MenuItem],
  templateUrl: './conversation-download-menu.html',
  styleUrl: './conversation-download-menu.scss',
})
export class ConversationDownloadMenu {
  private readonly exports = inject(ConversationExportStore);

  readonly conversationId = input.required<string>();
  /** Named in the trigger's accessible name, so the control says what it acts on. */
  readonly conversationName = input.required<string>();
  /** True while a turn is in flight. */
  readonly disabled = input<boolean>(false);
  /** `up` in the composer, which sits at the bottom of the screen. */
  readonly direction = input<'down' | 'up'>('down');

  protected readonly open = signal(false);
  /** Ties every item to the footer note through `aria-describedby`. */
  protected readonly noteId = `download-note-${nextId++}`;

  protected readonly options: readonly DownloadOption[] = EXPORT_FORMATS.map((format) => ({
    format,
    label: EXPORT_FORMAT_LABELS[format] ?? format,
    icon: ICONS[format],
  }));

  /** The format being prepared, but only for *this* conversation. */
  protected readonly preparing = computed<ExportFormat | null>(() => {
    const pending = this.exports.pending();

    return pending !== null && pending.conversationId === this.conversationId()
      ? pending.format
      : null;
  });

  /** Any conversation's: the store runs one export at a time and silently drops the rest. */
  protected readonly exportInFlight = computed(() => this.exports.pending() !== null);

  protected readonly triggerLabel = computed(() =>
    this.disabled()
      ? `Download ${this.conversationName()}. Available when the response finishes`
      : `Download ${this.conversationName()}`,
  );

  /** Per instance: the store's outcome is replaced, not consumed, and both menus can be mounted. */
  private lastHandledOutcome = 0;

  constructor() {
    effect(() => {
      const outcome = this.exports.outcome();

      if (outcome === null || outcome.seq === this.lastHandledOutcome) {
        return;
      }

      this.lastHandledOutcome = outcome.seq;

      // Only this conversation's, and only a success: a failure keeps the panel open so
      // the toast and the menu are on screen together.
      if (outcome.ok && outcome.conversationId === this.conversationId()) {
        this.open.set(false);
      }
    });
  }

  protected onChoose(format: ExportFormat): void {
    // Guarded as well as dimmed: `aria-disabled` items stay clickable by design, because
    // a natively disabled one could not be focused to read its state from.
    if (this.disabled() || this.exportInFlight()) {
      return;
    }

    this.exports.download(this.conversationId(), format);
  }
}

/** Each name must also be in `icon-names.ts`, or the sprite has no glyph to reference. */
const ICONS: Readonly<Record<ExportFormat, IconName>> = {
  md: 'bi-filetype-md',
  docx: 'bi-file-earmark-word',
  pdf: 'bi-file-earmark-pdf',
  html: 'bi-filetype-html',
};
