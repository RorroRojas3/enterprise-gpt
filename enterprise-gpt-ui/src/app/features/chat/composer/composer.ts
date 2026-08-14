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
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { SupportedExtensionsStore } from '@core/documents/supported-extensions-store';
import { SessionStore } from '@core/session/session-store';
import { AttachmentChip } from '@shared/chip/attachment-chip/attachment-chip';
import { Icon } from '@shared/icon/icon';
import { DropOverlay } from '@shared/upload/drop-overlay';
import { FileDropTarget } from '@shared/upload/file-drop-target';
import { TurnStore } from '../turn-store';
import { UploadStore } from '../upload-store';
import { DictationStore } from './dictation-store';
import { ModelMenu } from './model-menu';
import { ToolsMenu } from './tools-menu';

/**
 * The prompt box (frames `2a`–`2j`): attachment chips, textarea, warning line, and
 * the control row — attach, model pill, tools pill, microphone, send. While a turn is
 * in flight the send button gives way to the Stop control (frames `1b`, `2g`).
 *
 * The project and download controls are **absent, not disabled**, until their stories
 * land (US-307, US-1502) — the repo's pattern for unshipped affordances. When they
 * land, they take the `composer__aux` class so US-407's in-flight dimming applies
 * unchanged, as the attach control and the microphone now do.
 */
@Component({
  selector: 'app-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AttachmentChip, DropOverlay, FileDropTarget, Icon, ModelMenu, ToolsMenu],
  providers: [DictationStore],
  templateUrl: './composer.html',
  styleUrl: './composer.scss',
})
export class Composer {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly models = inject(ModelCatalogStore);
  protected readonly turn = inject(TurnStore);
  protected readonly uploads = inject(UploadStore);
  protected readonly extensions = inject(SupportedExtensionsStore);
  protected readonly session = inject(SessionStore);
  protected readonly dictation = inject(DictationStore);

  private readonly promptBox = viewChild<ElementRef<HTMLTextAreaElement>>('promptBox');
  private readonly actionButton = viewChild<ElementRef<HTMLButtonElement>>('actionButton');
  private readonly filePicker = viewChild<ElementRef<HTMLInputElement>>('filePicker');

  /** One field with no validation rules — a plain signal, not a form. */
  protected readonly prompt = signal('');

  /**
   * The amber line above the control row, one message at a time in order of
   * how much it costs the user to not know. Catalog failure (frame `2j`) wins
   * over everything: with no model there will be no turn, which makes the
   * rest moot. A restored model that no longer exists (US-410) comes next: it
   * explains a selection the user did not make and changes what the next turn
   * costs, where the cleared-tools warning (frame `2d`) explains one they did,
   * and a blocked microphone (US-413) only explains a control that failed. The
   * microphone message ranks last for that reason — one denial must not hide
   * the fact that the conversation is answering on a different model.
   *
   * EP-8 adds two rungs between those. "This model cannot read attachments" sits
   * near the top because it makes the attachment pointless *and* the answer
   * quietly ungrounded — retrieval is a tool call, so a model that cannot call
   * tools never sees the file. "Preparing files" sits below the selection
   * warnings and above the microphone: it explains why Send is disabled, which
   * the disabled control has already half-said.
   */
  protected readonly note = computed<string | null>(() => {
    if (this.models.error() !== null && this.settings.selectedModel() === null) {
      return 'The model catalog failed to load — sending is disabled. Retry from the model menu.';
    }

    if (this.settings.restoredModelUnavailable()) {
      return 'The model this conversation last used is unavailable — the default model is selected.';
    }

    // Retrieval reaches an attached document through a tool call, so a model that
    // cannot call tools cannot read one. `toolsDisabled` is the same computed the MCP
    // picker is gated on, for exactly the same underlying reason.
    if (this.uploads.hasAttachments() && this.settings.toolsDisabled()) {
      return 'This model can’t read attachments — pick a tool-enabled model, or the answer will ignore them.';
    }

    const cleared = this.settings.mcpSelectionClearedBy();
    if (cleared !== null) {
      return `${cleared.name} can't call tools — your previous selection was cleared.`;
    }

    if (this.uploads.blocksSend()) {
      const count = this.uploads.busyCount();
      // Describes the disabled control rather than promising an auto-send: nothing
      // sends when the files settle, the user still presses Send.
      return count === 1
        ? 'Preparing 1 file — Send will be available once it’s ready.'
        : `Preparing ${count} files — Send will be available once they’re ready.`;
    }

    if (this.dictation.permissionDenied()) {
      return 'Microphone access was blocked. Allow it in your browser to dictate, or type instead.';
    }

    return null;
  });

  /**
   * Null `streamSelection` covers the catalog pending, failed, and empty alike
   * — the app never sends without a `modelId` (frame `2j`). The empty-text
   * condition is US-401's criterion, and the attachment gate is US-801's: a
   * turn sent while a file is still being ingested would answer without it.
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

    // The picker's `accept` list is deployment-shaped and memoized for the session, so
    // the first composer to render pays for it and nothing else does. An effect rather
    // than a bare call, because `canUploadFiles` can flip mid-session when a grant is
    // edited — and a user who will never see the paperclip should not spend a request
    // on what it would have offered.
    effect(() => {
      if (this.session.canUploadFiles()) {
        void this.extensions.ensureLoaded();
      }
    });

    // The store seeds text — a prompt chip, a Retry restore — and the composer
    // consumes it; state flows one way and `protectedState` stays intact. The
    // focus follows the text so the user can edit or send immediately.
    effect(() => {
      const seed = this.turn.composerSeed();
      if (seed !== null) {
        this.prompt.set(seed.text);
        this.turn.consumeComposerSeed();
        this.promptBox()?.nativeElement.focus();
      }
    });

    // Dictated phrases append rather than replace, so speaking after typing —
    // or after an earlier phrase — extends the prompt instead of destroying
    // it. The store holds one phrase at a time and this consumes it, the same
    // one-way handoff `composerSeed` uses.
    effect(() => {
      const spoken = this.dictation.spoken();
      if (spoken !== null) {
        this.prompt.update((existing) =>
          existing === '' ? spoken.text : `${existing.trimEnd()} ${spoken.text}`,
        );
        this.dictation.consumeSpoken();
      }
    });

    // When a turn settles into an empty composer, the morphed Send re-disables
    // while focused — some engines then drop focus to <body>, others leave it
    // stranded on the disabled control; both arms are caught. The strand test
    // reads state, not the DOM: the effect runs before the binding writes
    // `disabled`. Only a fall-through is corrected — a user focused elsewhere
    // is left alone (the same rule as Chat's post-delete fixup).
    effect(() => {
      const inFlight = this.turn.inFlight();
      // A turn that settled into a notice leaves the transcript holding the
      // only thing worth acting on — its Retry — and the transcript restores
      // focus there itself. Claiming it for an empty prompt box would take the
      // user further from what they were doing, since the prompt a retry
      // re-sends lives in the notice, not in this box.
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

  protected onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.onFilesChosen(Array.from(input.files ?? []));
    // Cleared so that re-picking the same file fires `change` again — without this,
    // removing a chip and choosing the identical file is a silent no-op.
    input.value = '';
  }

  protected onFilesChosen(files: readonly File[]): void {
    if (files.length > 0) {
      this.uploads.attach(files);
    }
  }

  /** The morphing button's single handler: Stop in flight, Send otherwise. */
  protected onAction(): void {
    if (this.turn.inFlight()) {
      this.turn.stop();
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
