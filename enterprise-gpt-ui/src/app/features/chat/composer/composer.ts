import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { Icon } from '@shared/icon/icon';
import { ModelMenu } from './model-menu';
import { ToolsMenu } from './tools-menu';

/**
 * The prompt box (frames `2b`–`2j`): textarea, warning line, and the control
 * row — model pill, tools pill, send.
 *
 * The attach, project, mic, and download controls are **absent, not
 * disabled**, until their stories land (US-801, US-307, US-413, US-1502) —
 * the repo's pattern for unshipped affordances. Send carries its real disable
 * conditions but no click handler: US-401 wires the send itself.
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
   * condition is US-401's criterion, honest from day one.
   */
  protected readonly sendDisabled = computed(
    () => this.settings.streamSelection() === null || this.prompt().trim() === '',
  );

  protected onPromptInput(event: Event): void {
    this.prompt.set((event.target as HTMLTextAreaElement).value);
  }
}
