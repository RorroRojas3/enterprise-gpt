import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ModelDto } from '@domain/api/model';
import { providerMeta } from '@domain/api/provider';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { StatusDot, StatusTone } from '@shared/badge/status-dot/status-dot';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuTriggerContent } from '@shared/overlay/menu/menu-trigger-content';

/**
 * `400000 → '400k'`, `1500000 → '1.5M'` — the caption figure of frame `2b`.
 * Sub-thousand sizes render verbatim; non-integer thousands round.
 */
export function formatTokenCount(size: number): string {
  if (size >= 1_000_000) {
    const millions = size / 1_000_000;

    return `${Number.isInteger(millions) ? millions : millions.toFixed(1)}M`;
  }
  if (size >= 1_000) {
    return `${Math.round(size / 1_000)}k`;
  }

  return `${size}`;
}

/**
 * The composer's model pill and its picker (US-402, frame `2b`).
 *
 * The pill is the menu's projected trigger face; the rows are
 * `menuitemradio` items over the catalog in server order. A failed catalog is
 * frame `2j`: the pill reads "Models unavailable" and the panel offers Retry —
 * kept open through the reload so the rows appear in place when it lands.
 */
@Component({
  selector: 'app-model-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Menu, MenuItem, MenuTriggerContent, StatusDot],
  templateUrl: './model-menu.html',
  styleUrl: './model-menu.scss',
})
export class ModelMenu {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly models = inject(ModelCatalogStore);

  protected readonly pillName = computed(() => {
    const model = this.settings.selectedModel();
    if (model !== null) {
      return model.name;
    }

    // An error with a previously loaded catalog never lands here: the stale
    // catalog still resolves a model, which stays usable (only a cold failure
    // is frame 2j).
    if (this.models.error() !== null) {
      return 'Models unavailable';
    }

    return this.models.isFulfilled() ? 'No models' : 'Loading models…';
  });

  protected readonly pillTone = computed<StatusTone>(() => {
    const model = this.settings.selectedModel();

    return model === null ? 'muted' : (providerMeta(model.providerId)?.tone ?? 'muted');
  });

  /** The mono context figure beside the pill name; absent until a model resolves. */
  protected readonly pillContext = computed<string | null>(() => {
    const model = this.settings.selectedModel();

    return model === null ? null : formatTokenCount(model.contextWindowSize);
  });

  /** The trigger's accessible name carries the current value — it is all a screen reader gets. */
  protected readonly menuLabel = computed(() => `Model: ${this.pillName()}`);

  protected isSelected(model: ModelDto): boolean {
    return this.settings.selectedModel()?.id === model.id;
  }

  protected toneOf(model: ModelDto): StatusTone {
    return providerMeta(model.providerId)?.tone ?? 'muted';
  }

  /** `"Azure OpenAI · "`, or empty for a provider this build does not know. */
  protected captionPrefix(model: ModelDto): string {
    const provider = providerMeta(model.providerId);

    return provider === null ? '' : `${provider.name} · `;
  }

  protected contextOf(model: ModelDto): string {
    return formatTokenCount(model.contextWindowSize);
  }

  protected captionSuffix(model: ModelDto): string {
    return model.isToolEnabled ? ' context' : ' context · no tool calling';
  }
}
