import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { McpDto } from '@domain/api/mcp';
import { ModelDto } from '@domain/api/model';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { withResetOnSignOut } from '@core/state/with-reset-on-sign-out';

/**
 * The settings half of a turn request — spread into the stream client's
 * `StreamTurnRequest` beside the prompt and conversation id. `mcpServerIds` is
 * always present (`[]` when none apply); the transport maps it onto the wire's
 * `mcpServers: [{ id }]`, the field the old client dropped.
 */
export interface TurnSelection {
  readonly modelId: string;
  readonly mcpServerIds: readonly string[];
}

interface TurnSettingsState {
  /**
   * Null means "follow the catalog's default" — which is what makes
   * preselection declarative: the composer snaps to the `isDefault` model the
   * moment the catalog arrives, a reload that removes an explicit selection
   * falls back to the new default, and the sign-out reset restores
   * follow-default for the next session. No seeding effect exists anywhere.
   */
  readonly selectedModelId: string | null;
  readonly selectedMcpServerIds: readonly string[];
  /** The model whose selection cleared a non-empty MCP set (frame `2d`); null = no warning. */
  readonly mcpClearedByModelId: string | null;
}

const initialState: TurnSettingsState = {
  selectedModelId: null,
  selectedMcpServerIds: [],
  mcpClearedByModelId: null,
};

/**
 * The model and MCP servers the next turn will use (US-402, US-403).
 *
 * Root-provided because the selection persists for subsequent turns in the
 * session, across conversations — until US-410 starts seeding it per opened
 * conversation through {@link applyConversationSettings}. US-401 reads
 * `streamSelection` when it sends.
 *
 * The tool-gating invariant lives in two places on purpose:
 * `selectModel` performs the visible clear (frame `2d`'s "your previous
 * selection was cleared"), while `effectiveMcpServerIds` re-derives emptiness
 * whenever the selected model cannot call tools — so even a path that bypasses
 * `selectModel` (US-410 seeding stale data) can never produce the 400 the
 * server raises for MCP servers on a non-tool model.
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
     * Selection intersected with the permitted catalog. Load-bearing: US-410
     * will seed ids the caller may no longer be permitted, and a catalog
     * reload can drop a server mid-session — neither may reach the wire.
     */
    const selectedServers = computed<readonly McpDto[]>(() => {
      const ids = new Set(store.selectedMcpServerIds());

      return store._mcps.servers().filter((server) => ids.has(server.id));
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

      /** The Tools pill label, off the effective set so it never advertises servers that will not be sent. */
      toolsLabel: computed(() => {
        const count = effectiveMcpServerIds().length;

        return count === 0 ? 'Tools' : count === 1 ? '1 Tool' : `${count} Tools`;
      }),

      /** The model named by frame `2d`'s warning, or null when there is no warning to show. */
      mcpSelectionClearedBy: computed<ModelDto | null>(() => {
        const id = store.mcpClearedByModelId();
        if (id === null) {
          return null;
        }

        // A model removed by a catalog reload degrades to no warning rather
        // than naming a ghost.
        return store._models.models().find((model) => model.id === id) ?? null;
      }),

      /** O(1) row-check for the tools menu template. */
      selectedIdSet: computed(() => new Set(store.selectedMcpServerIds())),

      /**
       * Null while no model resolves (catalog pending, failed, or empty) —
       * the send control disables on null rather than sending without a
       * `modelId` (frame `2j`: the app never sends blind).
       */
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
      // The visible clear: masking instead would resurrect the selection on
      // the next tool-capable model, contradicting "was cleared". The warning
      // is sticky until the next selection changes the model again.
      const clears =
        next !== undefined && !next.isToolEnabled && store.selectedMcpServerIds().length > 0;

      patchState(store, {
        selectedModelId: modelId,
        selectedMcpServerIds: clears ? [] : store.selectedMcpServerIds(),
        mcpClearedByModelId: clears ? modelId : null,
      });
    },

    toggleMcpServer(serverId: string): void {
      // The pill is inert while tools are disabled; this guard keeps the state
      // honest for any caller that is not the pill.
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
     * The US-410 seeding surface: reopening a conversation restores the model
     * and servers it last used. `modelId: null` means follow the default.
     * Deliberately unvalidated — the joins absorb ids the catalogs no longer
     * carry, which is exactly the deep-link-before-catalog-loads case.
     */
    applyConversationSettings(settings: {
      modelId: string | null;
      mcpServerIds: readonly string[];
    }): void {
      patchState(store, {
        selectedModelId: settings.modelId,
        selectedMcpServerIds: [...settings.mcpServerIds],
        mcpClearedByModelId: null,
      });
    },
  })),
  withResetOnSignOut(),
);
