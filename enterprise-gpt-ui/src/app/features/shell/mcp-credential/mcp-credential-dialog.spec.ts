import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { McpDto } from '@domain/api/mcp';
import { McpCredentialStore } from '@core/catalog/mcp-credential-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { mcpFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { McpCredentialDialog } from './mcp-credential-dialog';

const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

function githubFixture(overrides: Partial<McpDto> = {}): McpDto {
  return mcpFixture({
    name: 'GitHub',
    requiresUserApiKey: true,
    hasUserApiKey: false,
    apiKeyHint: null,
    ...overrides,
  });
}

describe('McpCredentialDialog', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<McpCredentialDialog>;
  let credentials: InstanceType<typeof McpCredentialStore>;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    credentials = TestBed.inject(McpCredentialStore);

    fixture = TestBed.createComponent(McpCredentialDialog);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function field(): HTMLInputElement {
    return host().querySelector('#mcp-key-input') as HTMLInputElement;
  }

  function submitButton(): HTMLButtonElement {
    return host().querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  async function open(server: McpDto = githubFixture()): Promise<McpDto> {
    credentials.open(server);
    await fixture.whenStable();

    return server;
  }

  async function type(text: string): Promise<void> {
    const control = field();
    control.value = text;
    control.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  /** Leaves the field, which is what marks it touched — and what reveals its messages. */
  async function blur(): Promise<void> {
    field().dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  it('masks the field and keeps password managers out of it', async () => {
    await open();

    expect(field().type).toBe('password');
    expect(field().getAttribute('autocomplete')).toBe('off');
    expect(field().getAttribute('spellcheck')).toBe('false');
  });

  it('names the server and offers no removal until a key is stored', async () => {
    await open();

    expect(host().textContent).toContain('GitHub');
    expect(host().querySelector('.btn-outline-danger')).toBeNull();
    expect(submitButton().textContent?.trim()).toBe('Save key');
  });

  it('offers removal and a replace verb once a key is stored', async () => {
    await open(githubFixture({ hasUserApiKey: true, apiKeyHint: 'wxyz' }));

    expect(host().querySelector('.btn-outline-danger')?.textContent?.trim()).toBe('Remove key');
    expect(submitButton().textContent?.trim()).toBe('Replace key');
  });

  it('refuses a pasted token that caught surrounding whitespace', async () => {
    await open();
    await type('ghp_token_with a space');
    await blur();

    expect(host().querySelector('#mcp-key-errors')?.textContent).toContain(
      'Remove the spaces or line breaks',
    );

    // Enabled but inert: the button is the affordance that reveals the message, and pressing
    // it sends nothing.
    expect(submitButton().disabled).toBe(false);
    submitButton().click();
    await fixture.whenStable();
    backend.expectNone(() => true);
  });

  it('explains an empty field on Save rather than presenting a dead button', async () => {
    await open();

    submitButton().click();
    await fixture.whenStable();

    expect(host().querySelector('#mcp-key-errors')?.textContent).toContain(
      'Paste the token to continue.',
    );
    backend.expectNone(() => true);
  });

  it('submits the token exactly as typed', async () => {
    const server = await open();
    await type('github_pat_abcdwxyz');

    submitButton().click();
    await fixture.whenStable();

    const request = backend.expectOne(`${MCPS_URL}/${server.id}/credential`);
    expect(request.request.body).toEqual({ apiKey: 'github_pat_abcdwxyz' });
    request.flush({
      mcpServerId: server.id,
      hasApiKey: true,
      apiKeyHint: 'wxyz',
      dateModified: '2026-08-29T00:00:00+00:00',
    });
  });

  it('keeps the dialog open on a rejection and clears the message on the next keystroke', async () => {
    const server = await open();
    await type('github_pat_refused');
    submitButton().click();
    await fixture.whenStable();

    backend.expectOne(`${MCPS_URL}/${server.id}/credential`).flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { ApiKey: ['That token is not valid for this server.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    expect(host().querySelector('#mcp-key-errors')?.textContent).toContain(
      'That token is not valid for this server.',
    );

    await type('github_pat_another');

    expect(host().querySelector('#mcp-key-errors')).toBeNull();
  });

  it('reports a failure the field cannot carry in its own panel', async () => {
    const server = await open();
    await type('github_pat_abcdwxyz');
    submitButton().click();
    await fixture.whenStable();

    backend
      .expectOne(`${MCPS_URL}/${server.id}/credential`)
      .flush(PROBLEM_FIXTURES.forbidden, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    expect(host().querySelector('.mcp-key__summary')?.textContent).toBeTruthy();
    expect(host().querySelector('#mcp-key-input')).not.toBeNull();
  });

  it('starts empty on every open, so a stale key is never resubmitted', async () => {
    await open();
    await type('github_pat_abcdwxyz');
    credentials.close();
    await fixture.whenStable();

    await open(githubFixture({ name: 'Jira' }));

    expect(field().value).toBe('');
  });
});
