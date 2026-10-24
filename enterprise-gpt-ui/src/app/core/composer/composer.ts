import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { COMPOSER_HOST } from '@core/chat/composer-host.token';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { SessionStore } from '@core/session/session-store';
import { DictationStore } from '@state/composer/dictation-store';
import { COMPOSER_UPLOADS } from '@state/documents/composer-uploads.token';
import { SupportedExtensionsStore } from '@state/supported-extensions-store';
import { TurnSettingsStore } from '@state/turn-settings-store';
import { AttachmentChip } from '@shared/components/attachment-chip/attachment-chip';
import { ConversationDownloadMenu } from '@shared/components/conversation-download-menu/conversation-download-menu';
import { DropOverlay } from '@shared/components/drop-overlay/drop-overlay';
import { Icon } from '@shared/components/icon/icon';
import { ProjectPicker } from '@shared/components/project-picker/project-picker';
import { FileDropTarget } from '@shared/directives/file-drop-target';
import { ModelMenu } from './model-menu';
import { ToolsMenu } from './tools-menu';

/**
 * A host provides `COMPOSER_HOST`, which executes the prompt, and `COMPOSER_UPLOADS`. A pageless
 * feature because chat and the project screen both render it.
 */
@Component({
  selector: 'app-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AttachmentChip,
    ConversationDownloadMenu,
    DropOverlay,
    FileDropTarget,
    Icon,
    ModelMenu,
    ProjectPicker,
    ToolsMenu,
  ],
  providers: [DictationStore],
  templateUrl: './composer.html',
  styleUrl: './composer.scss',
  host: { class: 'd-block' },
})
export class Composer {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly models = inject(ModelCatalogStore);
  protected readonly turn = inject(COMPOSER_HOST);
  protected readonly uploads = inject(COMPOSER_UPLOADS);
  protected readonly extensions = inject(SupportedExtensionsStore);
  protected readonly session = inject(SessionStore);
  protected readonly dictation = inject(DictationStore);

  private readonly promptBox = viewChild<ElementRef<HTMLTextAreaElement>>('promptBox');
  private readonly actionButton = viewChild<ElementRef<HTMLButtonElement>>('actionButton');
  private readonly filePicker = viewChild<ElementRef<HTMLInputElement>>('filePicker');
  private readonly projectPill = viewChild<ElementRef<HTMLButtonElement>>('projectPill');

  private readonly projects = inject(ProjectLookupStore);

  protected readonly projectPickerOpen = signal(false);
  protected readonly projectPillEl = computed(() => this.projectPill()?.nativeElement ?? null);

  /**
   * Null when standalone, and also when the project is missing from the lookup set, where "No
   * project" would be a lie.
   */
  protected readonly projectLabel = computed(() => {
    const target = this.turn.projectTarget();
    if (target === null) {
      return null;
    }

    return this.projects.nameOf()(
      target.kind === 'conversation' ? target.conversation.projectId : target.projectId,
    );
  });

  /** One field with no validation rules — a plain signal, not a form. */
  protected readonly prompt = signal('');

  /** One warning at a time, ranked by how much not knowing it would cost the user. */
  protected readonly note = computed<string | null>(() => {
    if (this.models.error() !== null && this.settings.selectedModel() === null) {
      return 'The model catalog failed to load — sending is disabled. Retry from the model menu.';
    }

    if (this.settings.restoredModelUnavailable()) {
      return 'The model this conversation last used is unavailable — the default model is selected.';
    }

    // Retrieval is a tool call, so a model that cannot call tools cannot read an attachment.
    if (this.uploads.hasAttachments() && this.settings.toolsDisabled()) {
      return 'This model can’t read attachments — pick a tool-enabled model, or the answer will ignore them.';
    }

    const cleared = this.settings.mcpSelectionClearedBy();
    if (cleared !== null) {
      return `${cleared.name} can't call tools — your previous selection was cleared.`;
    }

    if (this.uploads.blocksSend()) {
      const count = this.uploads.busyCount();
      // Nothing sends itself when the files settle, so the copy names both Stop and Send.
      return count === 1
        ? 'Preparing 1 file — Stop cancels it, and Send returns once it’s ready.'
        : `Preparing ${count} files — Stop cancels them, and Send returns once they’re ready.`;
    }

    if (this.dictation.permissionDenied()) {
      return 'Microphone access was blocked. Allow it in your browser to dictate, or type instead.';
    }

    return null;
  });

  /**
   * Also true while files upload into an already-open conversation, which the host cannot see; the
   * control would otherwise sit disabled with nothing to press.
   */
  protected readonly inFlight = computed(() => this.turn.inFlight() || this.uploads.blocksSend());

  /** The microphone square, or the recording pill it morphs into; only the square dims mid-turn. */
  protected readonly micClass = computed(() =>
    this.dictation.recording()
      ? 'btn btn-sm rounded-pill d-inline-flex align-items-center gap-2 bg-info-subtle text-info-emphasis composer__mic-pill'
      : 'btn btn-ghost btn-icon composer__aux',
  );

  /** Named for what it actually stops, which is not always the answer. */
  protected readonly actionLabel = computed(() => {
    if (!this.inFlight()) {
      return 'Send';
    }

    return this.turn.inFlight() ? 'Stop' : 'Stop uploading';
  });

  /**
   * Null `streamSelection` covers the catalog pending, failed and empty alike. A turn sent while a
   * file is still ingesting would answer without it.
   */
  protected readonly sendDisabled = computed(
    () =>
      this.settings.streamSelection() === null ||
      this.prompt().trim() === '' ||
      this.uploads.blocksSend(),
  );

  /** For the focus fixup below; effects have no previous-value memory of their own. */
  private _wasInFlight = false;

  constructor() {
    const document = inject(DOCUMENT);

    // An effect, not a bare call: `canUploadFiles` can flip mid-session, and a user who never
    // sees the paperclip should not spend a request on it.
    effect(() => {
      if (this.session.canUploadFiles()) {
        void this.extensions.ensureLoaded();
      }
    });

    // The host seeds text (a prompt chip, a Retry restore) and the composer consumes it, keeping
    // `protectedState` intact; focus follows so the user can edit or send at once.
    effect(() => {
      const seed = this.turn.composerSeed();
      if (seed !== null) {
        this.prompt.set(seed.text);
        this.turn.consumeComposerSeed();
        this.promptBox()?.nativeElement.focus();
      }
    });

    // Dictated phrases append, so speaking after typing extends the prompt instead of replacing it.
    effect(() => {
      const spoken = this.dictation.spoken();
      if (spoken !== null) {
        this.prompt.update((existing) =>
          existing === '' ? spoken.text : `${existing.trimEnd()} ${spoken.text}`,
        );
        this.dictation.consumeSpoken();
      }
    });

    // A settled turn re-disables the focused Send, which drops focus to <body> in some engines and
    // strands it in others. The strand test reads state, since this runs before `disabled` is
    // written; a user focused elsewhere is left alone.
    effect(() => {
      const inFlight = this.inFlight();
      // After a notice, the transcript moves focus to its Retry itself.
      if (this._wasInFlight && !inFlight && this.turn.turnError() === null) {
        const active = document.activeElement;
        const button = this.actionButton()?.nativeElement;
        const fellThrough = active === null || active === document.body || !active.isConnected;
        const stranded =
          button !== undefined && active === button && untracked(() => this.sendDisabled());
        if (fellThrough || stranded) {
          this.promptBox()?.nativeElement.focus();
        }
      }
      this._wasInFlight = inFlight;
    });
  }

  protected onPromptInput(event: Event): void {
    this.prompt.set((event.target as HTMLTextAreaElement).value);
  }

  protected openFilePicker(): void {
    this.filePicker()?.nativeElement.click();
  }

  /**
   * A toggle: `onDismiss` ignores presses inside the host, so otherwise the pill could never close
   * the panel it opened.
   */
  protected toggleProjectPicker(): void {
    this.projectPickerOpen.update((open) => !open);
  }

  protected onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.onFilesChosen(Array.from(input.files ?? []));
    // Cleared so re-picking the same file fires `change` again.
    input.value = '';
  }

  protected onFilesChosen(files: readonly File[]): void {
    if (files.length > 0) {
      this.uploads.attach(files);
    }
  }

  /** The morphing button's single handler: Stop in flight, Send otherwise. */
  protected onAction(): void {
    if (this.inFlight()) {
      // The turn first: cancelling the uploads settles the gate synchronously, which would
      // release the waiting turn into the stream before `stop()` ran.
      this.turn.stop();
      this.uploads.cancelAll();
      return;
    }

    // Guarded even though the button disables: the store re-checks the
    // selection, and the prompt must not clear when nothing was sent.
    if (this.sendDisabled()) {
      return;
    }

    this.turn.send(this.prompt());
    this.prompt.set('');
  }
}
