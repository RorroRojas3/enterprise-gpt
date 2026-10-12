import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { ModelDto } from '@shared/models/api/chat-model.model';
import { McpDto } from '@shared/models/api/mcp.model';
import { needsUserApiKey } from '@shared/utils/catalog/mcp-server';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/** The settings half of a turn request; `mcpServerIds` is `[]`, never absent, when none apply. */
export interface TurnSelection {
  readonly modelId: string;
  readonly mcpServerIds: readonly string[];
}

interface TurnSettingsState {
  /** Null follows the catalog's default, so preselection needs no seeding effect. */
  readonly selectedModelId: string | null;
  readonly selectedMcpServerIds: readonly string[];
  /** The model whose selection cleared a non-empty MCP set; null means no warning. */
  readonly mcpClearedByModelId: string | null;
  /**
   * Kept apart from {@link selectedModelId} so a reopened conversation's deactivated model is
   * reported rather than silently replaced by the default. Null once the user picks a model.
   */
  readonly restoredModelId: string | null;
}

const initialState: TurnSettingsState = {
  selectedModelId: null,
  selectedMcpServerIds: [],
  mcpClearedByModelId: null,
  restoredModelId: null,
};

/**
 * The model and MCP servers the next turn will use; root-provided so the selection persists
 * across conversations. Tool gating is enforced twice on purpose: `selectModel` does the visible
 * clear, and `effectiveMcpServerIds` re-derives it so stale seeded ids never earn the server's
 * 400 for MCP servers on a non-tool model.
 */
export const TurnSettingsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withProps(() => ({
    _models: inject(ModelCatalogStore),
    _mcps: inject(McpCatalogStore),
  })),
  withComputed((store) => {
    const selectedModel = computed<ModelDto | null>(() => {
      const id = store.selectedModelId();
      const models = store._models.models();
      const explicit = id !== null ? models.find((model) => model.id === id) : undefined;

      return explicit ?? store._models.defaultModel();
    });

    const toolsDisabled = computed(() => {
      const model = selectedModel();

      return model !== null && !model.isToolEnabled;
    });

    /**
     * Seeded ids may no longer be permitted, and a selection can outlive the user's API key (the
     * server would answer 428), so neither may reach the wire.
     */
    const selectedServers = computed<readonly McpDto[]>(() => {
      const ids = new Set(store.selectedMcpServerIds());

      return store._mcps
        .servers()
        .filter((server) => ids.has(server.id) && !needsUserApiKey(server));
    });

    const effectiveMcpServerIds = computed<readonly string[]>(() =>
      toolsDisabled() || selectedModel() === null
        ? []
        : selectedServers().map((server) => server.id),
    );

    return {
      selectedModel,
      toolsDisabled,
      selectedServers,
      effectiveMcpServerIds,

      /** Counts the effective set, so it never advertises servers that will not be sent. */
      toolsLabel: computed(() => {
        const count = effectiveMcpServerIds().length;

        return count === 0 ? 'Tools' : count === 1 ? '1 Tool' : `${count} Tools`;
      }),

      mcpSelectionClearedBy: computed<ModelDto | null>(() => {
        const id = store.mcpClearedByModelId();
        if (id === null) {
          return null;
        }

        // A model removed by a catalog reload degrades to no warning rather
        // than naming a ghost.
        return store._models.models().find((model) => model.id === id) ?? null;
      }),

      selectedIdSet: computed(() => new Set(store.selectedMcpServerIds())),

      /**
       * Gated on the catalog resolving: on a deep link the empty pending `models()` would make
       * every restored model look deactivated and flash the warning.
       */
      restoredModelUnavailable: computed(() => {
        const id = store.restoredModelId();

        return (
          id !== null &&
          store._models.isFulfilled() &&
          !store._models.models().some((model) => model.id === id)
        );
      }),

      /** Null while no model resolves, which disables send rather than sending without a model. */
      streamSelection: computed<TurnSelection | null>(() => {
        const model = selectedModel();
        if (model === null) {
          return null;
        }

        return { modelId: model.id, mcpServerIds: effectiveMcpServerIds() };
      }),
    };
  }),
  withMethods((store) => ({
    selectModel(modelId: string): void {
      const next = store._models.models().find((model) => model.id === modelId);
      // Clear rather than mask: masking would resurrect the selection on the next tool-capable
      // model, contradicting "was cleared".
      const clears =
        next !== undefined && !next.isToolEnabled && store.selectedMcpServerIds().length > 0;

      patchState(store, {
        selectedModelId: modelId,
        selectedMcpServerIds: clears ? [] : store.selectedMcpServerIds(),
        mcpClearedByModelId: clears ? modelId : null,
        restoredModelId: null,
      });
    },

    toggleMcpServer(serverId: string): void {
      // The pill is already inert; this guards any other caller.
      if (store.toolsDisabled()) {
        return;
      }

      const current = store.selectedMcpServerIds();
      const next = current.includes(serverId)
        ? current.filter((id) => id !== serverId)
        : [...current, serverId];

      patchState(store, { selectedMcpServerIds: next, mcpClearedByModelId: null });
    },

    /**
     * Deliberately unvalidated: the joins absorb ids the catalog does not carry yet, as on a deep
     * link before it loads. `modelId: null` follows the default.
     */
    applyConversationSettings(settings: {
      modelId: string | null;
      mcpServerIds: readonly string[];
    }): void {
      patchState(store, {
        selectedModelId: settings.modelId,
        selectedMcpServerIds: [...settings.mcpServerIds],
        mcpClearedByModelId: null,
        restoredModelId: settings.modelId,
      });
    },

    /** Leaves the selection itself alone; it persists across conversations for the session. */
    clearRestoredModel(): void {
      patchState(store, { restoredModelId: null });
    },
  })),
  withResetOnSignOut(),
);
