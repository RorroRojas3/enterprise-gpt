import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture, modelFixture } from '@testing/catalog';
import { conversationFixture } from '@testing/conversations';
import { dispatchDrag, fileDrag, textFile } from '@testing/drag';
import { UPLOAD_FILE_GRANT, userFixture } from '@testing/session';
import { FakeRecognition } from '@testing/speech';
import { assistantEvent, frame, streamingResponse } from '@testing/stream-frames';
import { FakeUploadXhrQueue, provideFakeUploadXhr } from '@testing/upload-xhr';
import { ModelDto } from '@domain/api/model';
import { PermissionDto } from '@domain/api/user';
import { McpCatalogStore } from '@core/catalog/mcp-catalog-store';
import { ModelCatalogStore } from '@core/catalog/model-catalog-store';
import { TurnSettingsStore } from '@core/chat/turn-settings-store';
import { TokenService } from '@core/auth/token-service';
import { SupportedExtensionsStore } from '@core/documents/supported-extensions-store';
import { SessionStore } from '@core/session/session-store';
import { SPEECH_RECOGNITION, SpeechRecognitionLike } from '@core/speech/speech-recognition';
import { STREAM_FETCH } from '@core/stream/stream-fetch.token';
import { TurnStore } from '../turn-store';
import { UploadStore } from '../upload-store';
import { Composer } from './composer';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;
const CREATE_URL = `${TEST_API_BASE_URL}/api/conversations`;
const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const EXTENSIONS_URL = `${TEST_API_BASE_URL}/api/documents/file-extensions`;
const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
/** Exactly what the six registered extractors advertise. */
const SUPPORTED = ['.doc', '.docx', '.md', '.pdf', '.pptx', '.txt'];

describe('Composer', () => {
  let backend: HttpTestingController;
  let streamFetch: ReturnType<typeof vi.fn>;
  let hosts: HTMLElement[];
  // Read when the Composer is created, so a test can decide what this browser
  // can do after the module is configured. Null is jsdom's own answer.
  let speech: (() => SpeechRecognitionLike) | null;
  let uploadXhr: FakeUploadXhrQueue;

  beforeEach(() => {
    hosts = [];
    speech = null;
    uploadXhr = new FakeUploadXhrQueue();
    // Pending by default: composer specs assert the composer's side of a turn,
    // not the stream's, and an unresolved fetch keeps the turn in flight.
    streamFetch = vi.fn(() => new Promise<Response>(() => {}));

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: STREAM_FETCH, useValue: streamFetch },
        { provide: TokenService, useValue: { getToken: vi.fn().mockResolvedValue('token-1') } },
        { provide: Router, useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) } },
        { provide: SPEECH_RECOGNITION, useFactory: () => speech },
        provideFakeUploadXhr(uploadXhr),
        UploadStore,
        TurnStore,
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
    for (const host of hosts) {
      host.remove();
    }
  });

  async function render() {
    const fixture = TestBed.createComponent(Composer);
    document.body.append(fixture.nativeElement as HTMLElement);
    hosts.push(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      send: () => host.querySelector('.composer__send') as HTMLButtonElement | null,
      stop: () => host.querySelector('.composer__stop') as HTMLButtonElement | null,
      prompt: () => host.querySelector('.composer__prompt') as HTMLTextAreaElement,
      note: () => host.querySelector('.composer__note') as HTMLElement,
      async type(text: string) {
        const prompt = host.querySelector('.composer__prompt') as HTMLTextAreaElement;
        prompt.value = text;
        prompt.dispatchEvent(new Event('input', { bubbles: true }));
        await fixture.whenStable();
      },
    };
  }

  async function loadModels(models: ModelDto[]): Promise<void> {
    const loaded = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).flush(models);
    await loaded;
  }

  async function failModels(): Promise<void> {
    const failed = TestBed.inject(ModelCatalogStore).ensureLoaded();
    backend.expectOne(MODELS_URL).error(new ProgressEvent('error'));
    await failed;
  }

  it('disables send until both a model and text exist', async () => {
    const composer = await render();

    expect(composer.send()?.disabled).toBe(true);

    await composer.type('Hello there');
    // Text alone is not enough — no model has resolved yet.
    expect(composer.send()?.disabled).toBe(true);

    await loadModels([modelFixture({ isDefault: true })]);
    await composer.fixture.whenStable();
    expect(composer.send()?.disabled).toBe(false);

    await composer.type('   ');
    expect(composer.send()?.disabled).toBe(true);
  });

  it('renders frame 2j when the catalog fails: note, disabled send, unavailable pill', async () => {
    const composer = await render();
    await failModels();
    await composer.type('Still here');

    expect(composer.note().getAttribute('role')).toBe('status');
    expect(composer.note().textContent).toContain(
      'The model catalog failed to load — sending is disabled. Retry from the model menu.',
    );
    expect(composer.send()?.disabled).toBe(true);
    expect(composer.host.querySelector('.model-menu__pill')?.textContent).toContain(
      'Models unavailable',
    );
  });

  it("renders frame 2d's warning when a model change cleared the tool selection", async () => {
    const composer = await render();
    const capable = modelFixture({ isDefault: true });
    const limited = modelFixture({ name: 'GPT-5 Mini', isToolEnabled: false });
    await loadModels([capable, limited]);
    const serversLoaded = TestBed.inject(McpCatalogStore).ensureLoaded();
    const server = mcpFixture();
    backend.expectOne(MCPS_URL).flush([server]);
    await serversLoaded;

    const settings = TestBed.inject(TurnSettingsStore);
    settings.toggleMcpServer(server.id);
    settings.selectModel(limited.id);
    await composer.fixture.whenStable();

    expect(composer.note().textContent).toContain(
      "GPT-5 Mini can't call tools — your previous selection was cleared.",
    );

    settings.selectModel(capable.id);
    await composer.fixture.whenStable();
    expect(composer.note().textContent?.trim()).toBe('');
  });

  it('ships the still-unbuilt affordances as absent, not disabled', async () => {
    const composer = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await composer.fixture.whenStable();

    // Project (US-307) and download (US-1502). Attach shipped with US-801 and is
    // absent here for the other reason the repo allows: this session holds no
    // `Upload File` grant. The microphone is absent because this TestBed provides
    // no speech recognition.
    const buttons = [...composer.host.querySelectorAll('button')];
    const labels = buttons.map(
      (button) => button.getAttribute('aria-label') ?? button.textContent?.trim() ?? '',
    );
    expect(labels.some((label) => /project|download/i.test(label))).toBe(false);
    expect(composer.host.querySelector('.model-menu__pill')).not.toBeNull();
    expect(composer.send()).not.toBeNull();
  });

  describe('attachments (US-801)', () => {
    /** Resolves the session so `canUploadFiles` has something to answer from. */
    async function signIn(permissions: PermissionDto[] = [UPLOAD_FILE_GRANT]): Promise<void> {
      const loaded = TestBed.inject(SessionStore).ensureLoaded();
      backend.expectOne(ME_URL).flush(userFixture({ permissions }));
      await loaded;
    }

    /** The extensions list the composer fetches once the grant is visible. */
    async function flushExtensions(extensions = SUPPORTED): Promise<void> {
      backend.expectOne(EXTENSIONS_URL).flush(extensions);
      // The store memoizes its load, so this is the composer's own promise — awaiting
      // it is what settles the several microtasks between the flush and the patch.
      await TestBed.inject(SupportedExtensionsStore).ensureLoaded();
    }

    const attach = (host: HTMLElement) =>
      host.querySelector<HTMLButtonElement>('[aria-label="Attach files"]');
    const picker = (host: HTMLElement) =>
      host.querySelector<HTMLInputElement>('.composer__file-input');
    const chips = (host: HTMLElement) => [...host.querySelectorAll('app-attachment-chip')];

    it('renders no attach control and an inert drop zone without the grant (frame 2h)', async () => {
      const composer = await render();
      await signIn([]);
      await composer.fixture.whenStable();

      expect(attach(composer.host)).toBeNull();
      expect(picker(composer.host)).toBeNull();

      // The card still renders; it simply must not react. Nothing was fetched
      // either — a user who cannot upload does not pay for the picker's list.
      const card = composer.host.querySelector<HTMLElement>('.composer');
      dispatchDrag(card!, 'dragenter', fileDrag(textFile('notes.txt')));
      await composer.fixture.whenStable();
      expect(composer.host.querySelector('app-drop-overlay')).toBeNull();
    });

    it('constrains the picker to what this deployment can read', async () => {
      const composer = await render();
      await signIn();
      await composer.fixture.whenStable();
      await flushExtensions();
      await composer.fixture.whenStable();

      expect(attach(composer.host)).not.toBeNull();
      expect(picker(composer.host)?.getAttribute('accept')).toBe(SUPPORTED.join(','));
      expect(picker(composer.host)?.multiple).toBe(true);
    });

    it('chips a dropped file and uploads it against the open conversation', async () => {
      const composer = await render();
      await signIn();
      await composer.fixture.whenStable();
      await flushExtensions();
      TestBed.inject(UploadStore).bindConversation(CONVERSATION_ID);
      await composer.fixture.whenStable();

      const card = composer.host.querySelector<HTMLElement>('.composer');
      dispatchDrag(card!, 'drop', fileDrag(textFile('notes.txt')));
      await composer.fixture.whenStable();

      expect(chips(composer.host)).toHaveLength(1);
      expect(composer.host.textContent).toContain('notes.txt');
      expect(uploadXhr.opened).toEqual([
        ['POST', `${TEST_API_BASE_URL}/api/documents/conversations/${CONVERSATION_ID}`],
      ]);
    });

    it('refuses an unsupported file before any request, naming what would work', async () => {
      const composer = await render();
      await signIn();
      await composer.fixture.whenStable();
      await flushExtensions();
      TestBed.inject(UploadStore).bindConversation(CONVERSATION_ID);
      await composer.fixture.whenStable();

      const card = composer.host.querySelector<HTMLElement>('.composer');
      dispatchDrag(card!, 'drop', fileDrag(textFile('demo-capture.mov')));
      await composer.fixture.whenStable();

      expect(composer.host.textContent).toContain('.mov isn’t supported');
      expect(composer.host.textContent).toContain('PPTX');
      expect(uploadXhr.opened).toEqual([]);
    });

    it('holds Send until the files are ready, and says why', async () => {
      const composer = await render();
      await signIn();
      await loadModels([modelFixture({ isDefault: true })]);
      await composer.fixture.whenStable();
      await flushExtensions();
      TestBed.inject(UploadStore).bindConversation(CONVERSATION_ID);
      await composer.type('What does this say?');

      const card = composer.host.querySelector<HTMLElement>('.composer');
      dispatchDrag(card!, 'drop', fileDrag(textFile('notes.txt')));
      await composer.fixture.whenStable();

      expect(composer.send()?.disabled).toBe(true);
      expect(composer.note().textContent).toContain('Preparing 1 file');
    });

    it('warns that a model with no tools cannot read an attachment', async () => {
      const composer = await render();
      await signIn();
      await loadModels([modelFixture({ isDefault: true, isToolEnabled: false })]);
      await composer.fixture.whenStable();
      await flushExtensions();
      TestBed.inject(UploadStore).bindConversation(CONVERSATION_ID);
      await composer.fixture.whenStable();

      const card = composer.host.querySelector<HTMLElement>('.composer');
      dispatchDrag(card!, 'drop', fileDrag(textFile('notes.txt')));
      await composer.fixture.whenStable();

      // Retrieval is a tool call, so this model would answer without ever seeing
      // the file — the one thing worth saying before the ingest progress.
      expect(composer.note().textContent).toContain('can’t read attachments');
    });
  });

  describe('dictation (US-413)', () => {
    let recognition: FakeRecognition;

    beforeEach(() => {
      recognition = new FakeRecognition();
      speech = () => recognition;
    });

    function mic(host: HTMLElement): HTMLButtonElement | null {
      // By label rather than by class: US-801 moved the 34px square styling onto a
      // shared `composer__icon-button`, which the attach control wears too.
      return host.querySelector('[aria-label="Dictate"]');
    }

    function pill(host: HTMLElement): HTMLElement | null {
      return host.querySelector('.composer__mic-pill');
    }

    it('swaps the microphone for the recording pill and its clock (frame 2i)', async () => {
      const composer = await render();

      expect(mic(composer.host)).not.toBeNull();
      expect(pill(composer.host)).toBeNull();

      mic(composer.host)?.click();
      await composer.fixture.whenStable();

      // The pill takes the microphone's place, so nothing in the control row
      // moves when a session starts.
      expect(mic(composer.host)).toBeNull();
      const recording = pill(composer.host);
      expect(recording?.textContent).toContain('0:00');
      expect(recording?.querySelector('use')?.getAttribute('href')).toBe('#bi-mic-fill');
    });

    it('morphs one button rather than swapping two, so focus and state survive', async () => {
      const composer = await render();
      const button = mic(composer.host);
      button?.focus();

      button?.click();
      await composer.fixture.whenStable();

      // The same element: swapping would destroy the control the user just
      // pressed and drop focus to <body>, which is why the send control morphs
      // too. The changing label is what announces the state on a focused button.
      expect(pill(composer.host)).toBe(button);
      expect(document.activeElement).toBe(button);
      expect(button?.getAttribute('aria-label')).toBe('Stop dictating');

      button?.click();
      await composer.fixture.whenStable();
      expect(document.activeElement).toBe(button);
      expect(button?.getAttribute('aria-label')).toBe('Dictate');
    });

    it('appends a dictated phrase to what is already typed', async () => {
      const composer = await render();
      await composer.type('Summarize');
      mic(composer.host)?.click();
      await composer.fixture.whenStable();

      recognition.say({ text: 'the release notes', isFinal: true });
      await composer.fixture.whenStable();

      expect(composer.prompt().value).toBe('Summarize the release notes');
    });

    it('stops the session from the pill', async () => {
      const composer = await render();
      mic(composer.host)?.click();
      await composer.fixture.whenStable();

      pill(composer.host)?.click();
      await composer.fixture.whenStable();

      expect(recognition.stops).toBe(1);
      expect(mic(composer.host)).not.toBeNull();
    });

    it('disables the microphone while a turn is streaming', async () => {
      const composer = await render();
      await loadModels([modelFixture({ isDefault: true })]);
      await composer.type('Hello there');
      composer.send()?.click();
      await composer.fixture.whenStable();
      backend.expectOne(CREATE_URL).flush(conversationFixture());
      await composer.fixture.whenStable();

      expect(composer.stop()).not.toBeNull();
      expect(mic(composer.host)?.disabled).toBe(true);
      // US-407's dimming applies through the shared class rather than a rule
      // of the microphone's own.
      expect(mic(composer.host)?.classList.contains('composer__aux')).toBe(true);
    });

    it('explains a blocked microphone and leaves the composer usable', async () => {
      const composer = await render();
      mic(composer.host)?.click();
      await composer.fixture.whenStable();

      recognition.fail('not-allowed');
      await composer.fixture.whenStable();

      expect(composer.note().textContent).toContain('Microphone access was blocked');
      expect(composer.prompt().disabled).toBe(false);
      expect(mic(composer.host)).not.toBeNull();
    });

    it('renders no microphone at all where the browser has no engine', async () => {
      speech = null;
      const composer = await render();

      // Absent, never present-and-disabled: a control that can never work is
      // not a control.
      expect(mic(composer.host)).toBeNull();
      expect(pill(composer.host)).toBeNull();
    });
  });

  it('sends the trimmed prompt through the turn store and clears the box (US-401)', async () => {
    const composer = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await composer.type('  Hello there  ');

    composer.send()?.click();
    await composer.fixture.whenStable();

    // No conversation is bound, so the send opens with the create call.
    const create = backend.expectOne(CREATE_URL);
    expect(create.request.body).toEqual({ projectId: null });
    expect(composer.prompt().value).toBe('');

    create.flush({
      id: '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
      projectId: null,
      modelId: null,
      name: 'New conversation',
      isFavorite: false,
      dateCreated: '2026-08-12T09:00:00+00:00',
      dateModified: '2026-08-12T09:00:00+00:00',
    });
    await composer.fixture.whenStable();

    expect(streamFetch).toHaveBeenCalledTimes(1);
    const [, init] = streamFetch.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toMatchObject({ prompt: 'Hello there' });
  });

  it('swaps send for the Stop control while the turn is in flight (US-407)', async () => {
    const composer = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await composer.type('Hello there');

    composer.send()?.click();
    await composer.fixture.whenStable();

    // In flight: the 38px circular Stop replaces send outright.
    expect(composer.send()).toBeNull();
    const stop = composer.stop();
    expect(stop).not.toBeNull();
    expect(stop?.getAttribute('aria-label')).toBe('Stop');
    expect(stop?.querySelector('.composer__stop-glyph')).not.toBeNull();

    // Stopping during the create cancels it and hands the prompt back.
    const create = backend.expectOne(CREATE_URL);
    stop?.click();
    await composer.fixture.whenStable();

    expect(create.cancelled).toBe(true);
    expect(composer.send()).not.toBeNull();
    expect(composer.stop()).toBeNull();
    expect(composer.prompt().value).toBe('Hello there');
  });

  it('keeps keyboard focus on the morphing action button across the Send→Stop swap', async () => {
    const composer = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await composer.type('Hello there');

    const button = composer.send();
    button?.focus();
    button?.click();
    await composer.fixture.whenStable();

    // Same element, morphed — which is exactly why focus survives.
    expect(composer.stop()).toBe(button);
    expect(document.activeElement).toBe(button);

    const create = backend.expectOne(CREATE_URL);
    button?.click();
    await composer.fixture.whenStable();

    // Stop-during-create reseeds the prompt, and a seed hands focus to the
    // textarea so the user can edit and resend immediately.
    expect(create.cancelled).toBe(true);
    expect(composer.send()).toBe(button);
    expect(composer.prompt().value).toBe('Hello there');
    expect(document.activeElement).toBe(composer.prompt());
  });

  it('hands focus to the textarea when a turn settles into an empty composer', async () => {
    const handle = streamingResponse();
    streamFetch.mockImplementation((_input: unknown, init?: RequestInit) => {
      handle.abortOn(init?.signal);
      return Promise.resolve(handle.response);
    });
    const composer = await render();
    await loadModels([modelFixture({ isDefault: true })]);
    await composer.type('Hello there');

    const button = composer.send();
    button?.focus();
    button?.click();
    await composer.fixture.whenStable();
    backend.expectOne(CREATE_URL).flush(conversationFixture());
    await composer.fixture.whenStable();

    handle.enqueue(frame(assistantEvent('Finished')));
    handle.close();
    // Real timers here: wait out the transport's ~16ms buffer flush.
    await new Promise((resolve) => setTimeout(resolve, 40));
    await composer.fixture.whenStable();

    // Send re-disabled on the empty prompt; a focused-but-disabled button
    // would drop focus to <body>, so the composer hands it to the textarea.
    expect(composer.send()?.disabled).toBe(true);
    expect(document.activeElement).toBe(composer.prompt());
  });

  it('applies a store seed to the textarea, consumes it, and moves focus there', async () => {
    const composer = await render();

    TestBed.inject(TurnStore).seedComposer('Draft a status update');
    await composer.fixture.whenStable();

    expect(composer.prompt().value).toBe('Draft a status update');
    expect(TestBed.inject(TurnStore).composerSeed()).toBeNull();
    expect(document.activeElement).toBe(composer.prompt());
  });
});
