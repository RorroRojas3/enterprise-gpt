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
  EXPORT_FORMAT_LABELS,
  EXPORT_FORMATS,
  ExportFormat,
} from '@domain/api/conversation-export';
import { ConversationExportStore } from '@core/conversations/conversation-export-store';
import { Icon } from '@shared/icon/icon';
import { IconName } from '@shared/icon/icon-names';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';

let nextId = 0;

/** One row of the menu: what frame `2f` draws, in the order it draws it. */
interface DownloadOption {
  readonly format: ExportFormat;
  readonly label: string;
  readonly icon: IconName;
}

/**
 * The download control, and the one menu behind both of its triggers (US-1502, frame `2f`).
 *
 * Rendered in the composer's control row and in the conversation header, which is why it
 * lives in `shared/`: `features/chat` may be imported by neither, and the criterion asks
 * for *one* menu offered from both places rather than two that agree.
 *
 * Three behaviours are the whole story, and each is a criterion:
 *
 * - **The menu stays open while a format is preparing.** That is `Menu`'s own `stayOpen`,
 *   built for the multi-select tools picker and exactly right here: the chosen item shows
 *   its ring and the other two dim, which requires the panel to still be on screen.
 * - **It closes when the download starts, and not when it fails.** The store reports an
 *   outcome per settle; this closes on a successful one and leaves the panel up otherwise,
 *   so the reader can try another format without reopening.
 * - **While a turn streams the trigger is disabled with a reason.** `aria-disabled` rather
 *   than the native attribute, so the trigger keeps focus and can still be read.
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
  /** True while a turn is in flight (frame `2g`). */
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

  /**
   * Whether any export is running — this conversation's or another's.
   *
   * The store takes one at a time, and the dimming has to say so however the export was
   * started. Keying only on this conversation's would leave a second menu — after
   * navigating away from a conversation whose PDF is still rendering — offering three
   * live-looking items that the store silently drops.
   */
  protected readonly exportInFlight = computed(() => this.exports.pending() !== null);

  protected readonly triggerLabel = computed(() =>
    this.disabled()
      ? `Download ${this.conversationName()}. Available when the response finishes`
      : `Download ${this.conversationName()}`,
  );

  /**
   * The last outcome this instance acted on.
   *
   * Tracked per instance because the store's outcome is replaced rather than consumed:
   * the composer's menu and the header's menu are both mounted at desktop widths, and a
   * one-shot either of them cleared would be invisible to the other.
   */
  private lastHandledOutcome = 0;

  constructor() {
    effect(() => {
      const outcome = this.exports.outcome();

      if (outcome === null || outcome.seq === this.lastHandledOutcome) {
        return;
      }

      this.lastHandledOutcome = outcome.seq;

      // Only this conversation's, and only a success: a failure keeps the panel open so
      // the toast and the menu are on screen together (criterion 4).
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

/** Frame `2f`'s glyphs. All three are already in the sprite. */
const ICONS: Readonly<Record<ExportFormat, IconName>> = {
  md: 'bi-filetype-md',
  docx: 'bi-file-earmark-word',
  pdf: 'bi-file-earmark-pdf',
};
