import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PROVIDER_ID } from '@domain/api/provider';
import { ModelActionsStore } from '@core/catalog/model-actions-store';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { modelFixture } from '@testing/catalog';
import { installDialogPolyfill } from '@testing/dialog-polyfill';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { ModelFormDialog } from './model-form-dialog';

const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const BAD_REQUEST = { status: 400, statusText: 'Bad Request' };

describe('ModelFormDialog (US-1207)', () => {
  let backend: HttpTestingController;
  let fixture: ComponentFixture<ModelFormDialog>;
  let actions: InstanceType<typeof ModelActionsStore>;

  beforeEach(async () => {
    installDialogPolyfill();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    actions = TestBed.inject(ModelActionsStore);

    fixture = TestBed.createComponent(ModelFormDialog);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function field(id: string): HTMLInputElement | HTMLSelectElement {
    return host().querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;
  }

  function submitButton(): HTMLButtonElement {
    return host().querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  function errors(): string[] {
    return [...host().querySelectorAll('.model-form__error')].map(
      (node) => node.textContent?.trim() ?? '',
    );
  }

  async function type(id: string, text: string): Promise<void> {
    const control = field(id);
    control.value = text;
    control.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  /** Leaves the field, which is what marks it touched — and what reaching Save requires. */
  async function blur(id: string): Promise<void> {
    field(id).dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  async function choose(id: string, value: string): Promise<void> {
    const control = field(id) as HTMLSelectElement;
    control.value = value;
    // `input`, not `change`: the `[formField]` directive listens on `input` for every
    // native control, `<select>` included.
    control.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  /** Fills every required field with something valid. */
  async function fill(): Promise<void> {
    await choose('model-form-provider', PROVIDER_ID.anthropic);
    await type('model-form-name', 'Claude Sonnet 4.5');
    await type('model-form-deployment', 'claude-sonnet-4-5');
    await type('model-form-description', 'General-purpose reasoning model.');
    await type('model-form-context', '200000');
    await type('model-form-output', '64000');
  }

  async function openCreate(): Promise<void> {
    actions.beginCreate();
    await fixture.whenStable();
  }

  async function openEdit(model = modelFixture()): Promise<void> {
    actions.beginEdit(model);
    await fixture.whenStable();
  }

  async function submit(): Promise<TestRequest> {
    submitButton().click();
    await fixture.whenStable();

    return backend.expectOne((request) => request.url.startsWith(MODELS_URL));
  }

  it('binds the eight fields frame `5f` draws, plus the three the wire needs', async () => {
    await openCreate();

    for (const id of [
      'model-form-provider',
      'model-form-name',
      'model-form-deployment',
      'model-form-description',
      'model-form-context',
      'model-form-output',
      'model-form-tools',
      'model-form-default',
      // Not on the board, but on both request DTOs — and `ModelMapper` assigns them
      // unconditionally, so a form without them clears or falsifies stored values.
      'model-form-reasoning',
      'model-form-input-price',
      'model-form-output-price',
    ]) {
      expect(field(id), id).not.toBeNull();
    }
  });

  it('has no API key, endpoint URL or “available to” field', async () => {
    await openCreate();

    // Frame `5f`'s caption: none of the three exists in this system.
    const text = host().textContent ?? '';
    expect(text).not.toMatch(/API key/i);
    expect(text).not.toMatch(/endpoint/i);
    expect(text).not.toMatch(/available to/i);
    expect(host().querySelector('input[type="password"]')).toBeNull();
  });

  it('renders the three toggles as switches', async () => {
    await openCreate();

    for (const id of ['model-form-tools', 'model-form-reasoning', 'model-form-default']) {
      expect(field(id).getAttribute('role'), id).toBe('switch');
      expect(field(id).getAttribute('type'), id).toBe('checkbox');
    }
  });

  it('refuses a context window that is not a whole number, in the board’s words', async () => {
    await openCreate();
    await fill();
    await type('model-form-context', '400000.5');
    await blur('model-form-context');

    // The server would accept this: the column is decimal(18,2) and the validator carries
    // only GreaterThan(0). The rule is the client's, and `backend.verify()` proves the
    // request never went out.
    expect(errors()).toContain('Must be a whole number of tokens.');
    // The rejected text is still in the field, as frame `5f` draws it — which is why the
    // numeric fields are bound as text: `type="number"` would have discarded it.
    expect(field('model-form-context').value).toBe('400000.5');
    // And Save cannot send it.
    expect(submitButton().disabled).toBe(true);
  });

  it('refuses a negative price, and accepts a zero one', async () => {
    await openCreate();
    await fill();

    await type('model-form-input-price', '-1');
    await blur('model-form-input-price');
    expect(errors()).toContain('A price cannot be negative.');

    // Zero is a real price — a genuinely free deployment — which is why blank cannot be
    // normalised to it.
    await type('model-form-input-price', '0');
    expect(errors()).not.toContain('A price cannot be negative.');
    expect(submitButton().disabled).toBe(false);
  });

  it('sends a blank price as null, never as zero', async () => {
    await openCreate();
    await fill();
    const request = await submit();

    expect(request.request.body).toMatchObject({
      inputPricePerMillionTokens: null,
      outputPricePerMillionTokens: null,
    });
    request.flush(modelFixture());
    await fixture.whenStable();
  });

  it('sends a typed price as a number', async () => {
    await openCreate();
    await fill();
    await type('model-form-input-price', '3');
    await type('model-form-output-price', '15');
    const request = await submit();

    expect(request.request.body).toMatchObject({
      inputPricePerMillionTokens: 3,
      outputPricePerMillionTokens: 15,
    });
    request.flush(modelFixture());
    await fixture.whenStable();
  });

  it('echoes reasoning and both prices unchanged when an edit touches neither', async () => {
    const target = modelFixture({
      isReasoningEnabled: true,
      inputPricePerMillionTokens: 1.25,
      outputPricePerMillionTokens: 10,
    });
    await openEdit(target);
    await type('model-form-name', 'Renamed');
    const request = await submit();

    // The PUT is a full representation: `ModelMapper` assigns every field, so a body
    // without these three would clear the prices and read reasoning as false.
    expect(request.request.body).toMatchObject({
      name: 'Renamed',
      isReasoningEnabled: true,
      inputPricePerMillionTokens: 1.25,
      outputPricePerMillionTokens: 10,
    });
    request.flush(target);
    await fixture.whenStable();
  });

  it('seeds an unpriced model as blank fields rather than zeros', async () => {
    await openEdit(
      modelFixture({ inputPricePerMillionTokens: null, outputPricePerMillionTokens: null }),
    );

    expect(field('model-form-input-price').value).toBe('');
    expect(field('model-form-output-price').value).toBe('');
  });

  it('renders the server’s message at the field it belongs to, and clears it on the next edit', async () => {
    await openCreate();
    await fill();
    const request = await submit();
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { DeploymentName: ['That deployment name is already in use.'] },
      },
      BAD_REQUEST,
    );
    await fixture.whenStable();

    expect(errors()).toContain('That deployment name is already in use.');
    expect(field('model-form-deployment').classList).toContain('is-invalid');
    // The message belongs to `deploymentName`, so no other field wears it.
    expect(field('model-form-name').classList).not.toContain('is-invalid');

    // Signal Forms has no `setErrors`, so the message is reported by a rule that only
    // fires while the field still holds the exact value that was sent.
    await type('model-form-deployment', 'claude-sonnet-4-5-v2');
    expect(errors()).not.toContain('That deployment name is already in use.');
  });

  it('shows a message the form does not model rather than dropping it', async () => {
    await openCreate();
    await fill();
    const request = await submit();
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { '': ['The catalog is at its limit for this deployment.'] },
      },
      BAD_REQUEST,
    );
    await fixture.whenStable();

    // A server message the reader cannot see is a save that silently fails.
    expect(errors()).toContain('The catalog is at its limit for this deployment.');
  });

  it('renders a create’s provider 404 at the Provider field', async () => {
    await openCreate();
    await fill();
    const request = await submit();
    request.flush(
      { ...PROBLEM_FIXTURES.resourceNotFound, detail: 'Provider not found.' },
      { status: 404, statusText: 'Not Found' },
    );
    await fixture.whenStable();

    // `EnsureProviderExistsAsync` throws this for a provider the deployment has
    // deactivated since this build's list was written. It carries no `errors` dictionary,
    // so it cannot travel the `serverMessagesFor` path — but it is about this field.
    expect(errors()).toContain('Provider not found.');
    expect(field('model-form-provider').classList).toContain('is-invalid');
  });

  it('keeps every typed field when a save fails for a non-validation reason', async () => {
    await openCreate();
    await fill();
    await type('model-form-description', 'A description worth not retyping.');
    const request = await submit();
    request.error(new ProgressEvent('error'));
    await fixture.whenStable();

    // Eleven fields behind a dialog that used to close on any non-validation failure, and
    // `retryInterceptor` does not retry a POST. The reader keeps what they typed.
    expect(field('model-form-name').value).toBe('Claude Sonnet 4.5');
    expect(field('model-form-description').value).toBe('A description worth not retyping.');
    expect(submitButton().disabled).toBe(false);
  });

  it('shows a message keyed to a toggle, which no field here can render', async () => {
    await openCreate();
    await fill();
    const request = await submit();
    request.flush(
      {
        ...PROBLEM_FIXTURES.validationError,
        errors: { IsDefault: ['A default model must have tools enabled.'] },
      },
      BAD_REQUEST,
    );
    await fixture.whenStable();

    // The three toggles are bound but have no `validate` rule and no `fieldErrors()`
    // entry, so treating them as "modelled" would mark this matched and render it nowhere.
    expect(errors()).toContain('A default model must have tools enabled.');
  });

  it('opens on edit with the model’s values, and on create with none of them', async () => {
    await openEdit(modelFixture({ name: 'GPT-5 Enterprise', deploymentName: 'gpt-5-ent' }));
    expect(field('model-form-name').value).toBe('GPT-5 Enterprise');

    actions.cancelForm();
    await fixture.whenStable();
    await openCreate();

    // The component is mounted once and lives as long as the tab, so a second open has
    // to reseed rather than inherit.
    expect(field('model-form-name').value).toBe('');
    expect(field('model-form-deployment').value).toBe('');
    // The column's own default: a model that cannot call tools is the exception.
    expect((field('model-form-tools') as HTMLInputElement).checked).toBe(true);
  });

  it('names the verb it is about to perform', async () => {
    await openCreate();
    expect(host().textContent).toContain('Add model');

    actions.cancelForm();
    await fixture.whenStable();
    await openEdit();
    expect(host().textContent).toContain('Edit model');
    expect(host().textContent).toContain('Save model');
  });

  it('offers Azure AI Foundry, which the client-side mirror had been missing', async () => {
    await openCreate();

    const options = [...host().querySelectorAll('#model-form-provider option')].map((node) =>
      node.textContent?.trim(),
    );
    // There is no `GET api/providers`, so this list is the only source there is.
    expect(options).toContain('Azure AI Foundry');
    expect(options).toContain('Anthropic');
  });

  it('keeps Save disabled until the required fields are filled', async () => {
    await openCreate();
    expect(submitButton().disabled).toBe(true);

    await fill();
    expect(submitButton().disabled).toBe(false);
  });
});
