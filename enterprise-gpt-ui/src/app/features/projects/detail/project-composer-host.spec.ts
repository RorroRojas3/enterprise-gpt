import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PendingPromptStore } from '@core/chat/pending-prompt-store';
import { ToastStore } from '@core/notifications/toast-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { conversationFixture } from '@testing/conversations';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ProjectComposerHost } from './project-composer-host';
import { ProjectStore } from './project-store';

const CREATE_URL = `${TEST_API_BASE_URL}/api/conversations`;
const PROJECT_ID = '11111111-5555-4666-8777-888888888888';

describe('ProjectComposerHost (US-906)', () => {
  let backend: HttpTestingController;
  let host: InstanceType<typeof ProjectComposerHost>;
  let pending: InstanceType<typeof PendingPromptStore>;
  let navigate: ReturnType<typeof vi.fn>;
  let projectId: string | null;

  beforeEach(() => {
    projectId = PROJECT_ID;
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate } },
        { provide: ProjectStore, useValue: { projectId: () => projectId } },
        ProjectComposerHost,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    host = TestBed.inject(ProjectComposerHost);
    pending = TestBed.inject(PendingPromptStore);
  });

  afterEach(() => {
    backend.verify();
  });

  function expectCreate(): TestRequest {
    return backend.expectOne((request) => request.url === CREATE_URL);
  }

  it('creates the conversation inside the project and hands the prompt to the chat route', () => {
    const created = conversationFixture({ projectId: PROJECT_ID });

    host.send('  Draft the cutover comms plan  ');
    TestBed.tick();

    const request = expectCreate();
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ projectId: PROJECT_ID });
    expect(host.inFlight()).toBe(true);

    request.flush(created);
    TestBed.tick();

    // The prompt travels in a store rather than in the URL: a prompt in the address
    // bar is a prompt in the history, and in every referrer the page sends.
    expect(pending.claim(created.id)).toBe('Draft the cutover comms plan');
    expect(navigate).toHaveBeenCalledWith(['/chat', created.id]);
    expect(host.inFlight()).toBe(false);
  });

  it('never streams here — turnError stays null so the composer behaves as on chat', () => {
    expect(host.turnError()).toBeNull();
  });

  it('drops a second press while the create is out, rather than making two conversations', () => {
    host.send('First');
    TestBed.tick();
    const request = expectCreate();

    host.send('Second');
    TestBed.tick();

    request.flush(conversationFixture());
  });

  it('cancels the create on Stop and hands the prompt back', () => {
    host.send('Draft the plan');
    TestBed.tick();
    const request = expectCreate();

    host.stop();
    TestBed.tick();

    expect(request.cancelled).toBe(true);
    expect(host.inFlight()).toBe(false);
    expect(host.composerSeed()).toEqual({ text: 'Draft the plan' });
    expect(navigate).not.toHaveBeenCalled();
  });

  it('sends the reader back to the grid when the project has been deleted elsewhere', () => {
    const toasts = TestBed.inject(ToastStore);

    host.send('Draft the plan');
    TestBed.tick();
    expectCreate().flush(PROBLEM_FIXTURES.resourceNotFound, {
      status: 404,
      statusText: 'Not Found',
    });
    TestBed.tick();

    expect(toasts.toasts()[0]?.title).toBe('This project no longer exists');
    expect(navigate).toHaveBeenCalledWith(['/projects']);
    // Nothing to hand back: the URL the reader was on names nothing now.
    expect(host.composerSeed()).toBeNull();
  });

  it('returns the prompt to the composer when the create fails for any other reason', () => {
    host.send('Draft the plan');
    TestBed.tick();
    expectCreate().flush(PROBLEM_FIXTURES.validationError, {
      status: 400,
      statusText: 'Bad Request',
    });
    TestBed.tick();

    expect(host.composerSeed()).toEqual({ text: 'Draft the plan' });
    expect(navigate).not.toHaveBeenCalled();
  });

  it('refuses to send before the route has resolved a project', () => {
    projectId = null;

    host.send('Draft the plan');
    TestBed.tick();

    backend.verify();
  });
});
