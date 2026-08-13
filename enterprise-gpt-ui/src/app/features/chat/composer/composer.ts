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
import { Icon } from '@shared/icon/icon';
import { TurnStore } from '../turn-store';
import { ModelMenu } from './model-menu';
import { ToolsMenu } from './tools-menu';

/**
 * The prompt box (frames `2b`–`2j`): textarea, warning line, and the control
 * row — model pill, tools pill, send. While a turn is in flight the send
 * button gives way to the Stop control (frames `1b`, `2g`).
 *
 * The attach, project, mic, and download controls are **absent, not
 * disabled**, until their stories land (US-801, US-307, US-413, US-1502) —
 * the repo's pattern for unshipped affordances. When they land, they take the
 * `composer__aux` class so US-407's in-flight dimming applies unchanged.
 */
@Component({
  selector: 'app-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, ModelMenu, ToolsMenu],
  templateUrl: './composer.html',
  styleUrl: './composer.scss',
})
export class Composer {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly models = inject(ModelCatalogStore);
  protected readonly turn = inject(TurnStore);

  private readonly promptBox = viewChild<ElementRef<HTMLTextAreaElement>>('promptBox');
  private readonly actionButton = viewChild<ElementRef<HTMLButtonElement>>('actionButton');

  /** One field with no validation rules — a plain signal, not a form. */
  protected readonly prompt = signal('');

  /**
   * The amber line above the control row. Catalog failure (frame `2j`) wins
   * over the cleared-tools warning (frame `2d`): with no model there will be
   * no turn, which makes the tools state moot.
   */
  protected readonly note = computed<string | null>(() => {
    if (this.models.error() !== null && this.settings.selectedModel() === null) {
      return 'The model catalog failed to load — sending is disabled. Retry from the model menu.';
    }

    const cleared = this.settings.mcpSelectionClearedBy();
    if (cleared !== null) {
      return `${cleared.name} can't call tools — your previous selection was cleared.`;
    }

    return null;
  });

  /**
   * Null `streamSelection` covers the catalog pending, failed, and empty alike
   * — the app never sends without a `modelId` (frame `2j`). The empty-text
   * condition is US-401's criterion.
   */
  protected readonly sendDisabled = computed(
    () => this.settings.streamSelection() === null || this.prompt().trim() === '',
  );

  /** For the focus fixup below; effects have no previous-value memory of their own. */
  private _wasInFlight = false;

  constructor() {
    const document = inject(DOCUMENT);

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

    // When a turn settles into an empty composer, the morphed Send re-disables
    // while focused — some engines then drop focus to <body>, others leave it
    // stranded on the disabled control; both arms are caught. The strand test
    // reads state, not the DOM: the effect runs before the binding writes
    // `disabled`. Only a fall-through is corrected — a user focused elsewhere
    // is left alone (the same rule as Chat's post-delete fixup).
    effect(() => {
      const inFlight = this.turn.inFlight();
      if (this._wasInFlight && !inFlight) {
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
