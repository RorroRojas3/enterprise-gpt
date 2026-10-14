import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { providerMeta } from '@shared/utils/catalog/provider-meta';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@state/turn-settings-store';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { StatusDot, StatusTone } from '@shared/components/status-dot/status-dot';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuTriggerContent } from '@shared/directives/menu-trigger-content';

/** `400000 → '400k'`, `1500000 → '1.5M'`; sub-thousand sizes render verbatim. */
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
 * On a failed catalog the pill reads "Models unavailable" and the panel offers Retry, staying open
 * through the reload so the rows appear in place.
 */
@Component({
  selector: 'app-model-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Menu, MenuItem, MenuTriggerContent, StatusDot],
  templateUrl: './model-menu.html',
  // `min-w-0` here and on the menu: the pill is the row's elastic item, and both links are needed.
  host: { class: 'd-inline-flex min-w-0' },
})
export class ModelMenu {
  protected readonly settings = inject(TurnSettingsStore);
  protected readonly models = inject(ModelCatalogStore);

  protected readonly pillName = computed(() => {
    const model = this.settings.selectedModel();
    if (model !== null) {
      return model.name;
    }

    // A stale catalog still resolves a usable model, so only a cold failure lands here.
    if (this.models.error() !== null) {
      return 'Models unavailable';
    }

    return this.models.isFulfilled() ? 'No models' : 'Loading models…';
  });

  protected readonly pillTone = computed<StatusTone>(() => {
    const model = this.settings.selectedModel();

    return model === null ? 'secondary' : (providerMeta(model.providerId)?.tone ?? 'secondary');
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
    return providerMeta(model.providerId)?.tone ?? 'secondary';
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
