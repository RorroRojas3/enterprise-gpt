import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import { McpDto } from '@domain/api/mcp';
import { ModelDto } from '@domain/api/model';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { sessionEvents } from '@core/events/session-events';
import { TurnSettingsStore } from './turn-settings-store';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

describe('TurnSettingsStore', () => {
  let store: InstanceType<typeof TurnSettingsStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    store = TestBed.inject(TurnSettingsStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  async function loadModels(models: ModelDto[]): Promise<void> {
    const loaded = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush(models);
    await loaded;
  }

  async function reloadModels(models: ModelDto[]): Promise<void> {
    const reloaded = TestBed.inject(ModelCatalogStore).reload();
    backend.expectOne(MODELS_URL).flush(models);
    await reloaded;
  }

  async function loadServers(servers: McpDto[]): Promise<void> {
    const loaded = TestBed.inject(McpCatalogStore).ensureLoaded();
    backend.expectOne(MCPS_URL).flush(servers);
    await loaded;
  }

  it('resolves no model and a null streamSelection before the catalog arrives', () => {
    expect(store.selectedModel()).toBeNull();
    expect(store.streamSelection()).toBeNull();
    expect(store.toolsDisabled()).toBe(false);
    expect(store.toolsLabel()).toBe('Tools');
  });

  it('preselects the default model when the catalog loads late, with no method call', async () => {
    const standard = modelFixture();
    const preferred = modelFixture({ isDefault: true });

    await loadModels([standard, preferred]);

    expect(store.selectedModel()).toEqual(preferred);
    expect(store.streamSelection()).toEqual({ modelId: preferred.id, mcpServerIds: [] });
  });

  it('persists an explicit selection across reads and a same-set reload', async () => {
    const preferred = modelFixture({ isDefault: true });
    const chosen = modelFixture();
    await loadModels([preferred, chosen]);

    store.selectModel(chosen.id);
    expect(store.selectedModel()).toEqual(chosen);

    await reloadModels([preferred, chosen]);
    expect(store.selectedModel()).toEqual(chosen);
  });

  it('falls back to the new default when a reload removes the selected model', async () => {
    const preferred = modelFixture({ isDefault: true });
    const retired = modelFixture();
    await loadModels([preferred, retired]);
    store.selectModel(retired.id);

    const successor = modelFixture({ isDefault: true });
    await reloadModels([successor]);

    expect(store.selectedModel()).toEqual(successor);
    expect(store.streamSelection()).toEqual({ modelId: successor.id, mcpServerIds: [] });
  });

  it('toggles servers independently and labels the pill from the effective set', async () => {
    await loadModels([modelFixture({ isDefault: true })]);
    const [first, second] = [mcpFixture(), mcpFixture()];
    await loadServers([first, second]);

    expect(store.toolsLabel()).toBe('Tools');

    store.toggleMcpServer(first.id);
    expect(store.toolsLabel()).toBe('1 Tool');

    store.toggleMcpServer(second.id);
    expect(store.toolsLabel()).toBe('2 Tools');
    expect(store.selectedIdSet().has(first.id)).toBe(true);

    store.toggleMcpServer(first.id);
    expect(store.toolsLabel()).toBe('1 Tool');
    expect(store.selectedIdSet().has(first.id)).toBe(false);
    expect(store.selectedIdSet().has(second.id)).toBe(true);
  });

  it('clears the selection and raises the warning when a non-tool model is chosen', async () => {
    const capable = modelFixture({ isDefault: true });
    const limited = modelFixture({ isToolEnabled: false });
    await loadModels([capable, limited]);
    const [first, second] = [mcpFixture(), mcpFixture()];
    await loadServers([first, second]);

    store.toggleMcpServer(first.id);
    store.toggleMcpServer(second.id);
    expect(store.toolsLabel()).toBe('2 Tools');

    store.selectModel(limited.id);

    expect(store.selectedMcpServerIds()).toEqual([]);
    expect(store.mcpSelectionClearedBy()).toEqual(limited);
    expect(store.toolsDisabled()).toBe(true);
    expect(store.toolsLabel()).toBe('Tools');
    expect(store.streamSelection()).toEqual({ modelId: limited.id, mcpServerIds: [] });
  });

  it('drops the warning when the model changes again, and never raises it over an empty selection', async () => {
    const capable = modelFixture({ isDefault: true });
    const limited = modelFixture({ isToolEnabled: false });
    await loadModels([capable, limited]);
    const server = mcpFixture();
    await loadServers([server]);

    store.selectModel(limited.id);
    expect(store.mcpSelectionClearedBy()).toBeNull();

    store.selectModel(capable.id);
    store.toggleMcpServer(server.id);
    store.selectModel(limited.id);
    expect(store.mcpSelectionClearedBy()).toEqual(limited);

    store.selectModel(capable.id);
    expect(store.mcpSelectionClearedBy()).toBeNull();
  });

  it('ignores toggles while tools are disabled', async () => {
    const limited = modelFixture({ isDefault: true, isToolEnabled: false });
    await loadModels([limited]);
    const server = mcpFixture();
    await loadServers([server]);

    store.toggleMcpServer(server.id);

    expect(store.selectedMcpServerIds()).toEqual([]);
  });

  it('sends no servers when settings seed a non-tool model with server ids', async () => {
    // The path that bypasses selectModel's visible clear: US-410 restoring a
    // conversation whose stored settings pair a non-tool model with servers.
    // effectiveMcpServerIds must re-derive emptiness, or the server 400s.
    const limited = modelFixture({ isDefault: true, isToolEnabled: false });
    await loadModels([limited]);
    const server = mcpFixture();
    await loadServers([server]);

    store.applyConversationSettings({ modelId: limited.id, mcpServerIds: [server.id] });

    expect(store.selectedMcpServerIds()).toEqual([server.id]);
    expect(store.streamSelection()).toEqual({ modelId: limited.id, mcpServerIds: [] });
    expect(store.toolsLabel()).toBe('Tools');
  });

  it('keeps unpermitted and unknown server ids out of the wire selection', async () => {
    const capable = modelFixture({ isDefault: true });
    await loadModels([capable]);
    const permitted = mcpFixture();
    await loadServers([permitted]);

    store.applyConversationSettings({
      modelId: capable.id,
      mcpServerIds: [permitted.id, 'ghost-mcp-id'],
    });

    expect(store.streamSelection()).toEqual({
      modelId: capable.id,
      mcpServerIds: [permitted.id],
    });
  });

  it('follows the default again when settings seed a null model id', async () => {
    const preferred = modelFixture({ isDefault: true });
    const other = modelFixture();
    await loadModels([preferred, other]);

    store.selectModel(other.id);
    store.applyConversationSettings({ modelId: null, mcpServerIds: [] });

    expect(store.selectedModel()).toEqual(preferred);
    expect(store.mcpSelectionClearedBy()).toBeNull();
  });

  it('resets to follow-default on sign-out', async () => {
    const capable = modelFixture({ isDefault: true });
    const other = modelFixture();
    await loadModels([capable, other]);
    const server = mcpFixture();
    await loadServers([server]);

    store.selectModel(other.id);
    store.toggleMcpServer(server.id);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());

    // The catalogs reset with the session, so nothing resolves until the next
    // sign-in reloads them.
    expect(store.selectedModel()).toBeNull();
    expect(store.selectedMcpServerIds()).toEqual([]);
    expect(store.streamSelection()).toBeNull();
    expect(store.toolsLabel()).toBe('Tools');
  });
});
