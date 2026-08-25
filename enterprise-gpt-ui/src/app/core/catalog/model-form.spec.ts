import { describe, expect, it } from 'vitest';
import { modelFixture } from '@testing/catalog';
import { PROVIDER_ID } from '@domain/api/provider';
import {
  ModelFormValue,
  PROVIDER_OPTIONS,
  WHOLE_TOKENS_MESSAGE,
  priceError,
  toModelBody,
  toModelFormValue,
  tokenCountError,
} from './model-form';

/** A complete, valid form value; each test spoils one field of it. */
function formValue(overrides: Partial<ModelFormValue> = {}): ModelFormValue {
  return {
    providerId: PROVIDER_ID.anthropic,
    name: 'Claude Sonnet 4.5',
    deploymentName: 'claude-sonnet-4-5',
    description: 'General-purpose reasoning model.',
    contextWindowSize: '200000',
    maxOutputTokens: '64000',
    inputPricePerMillionTokens: '3',
    outputPricePerMillionTokens: '15',
    isToolEnabled: true,
    isReasoningEnabled: true,
    isUserSelectable: true,
    isDefault: false,
    ...overrides,
  };
}

describe('tokenCountError', () => {
  it('refuses a fractional count in the board’s own words', () => {
    // The columns are decimal(18,2) and the validator carries only GreaterThan(0), so
    // the server would accept this and store it. The rule is the client's.
    expect(tokenCountError('400000.5')).toBe(WHOLE_TOKENS_MESSAGE);
  });

  it('refuses text that is not a number at all', () => {
    expect(tokenCountError('400k')).toBe(WHOLE_TOKENS_MESSAGE);
    expect(tokenCountError('400,000')).toBe(WHOLE_TOKENS_MESSAGE);
  });

  it('refuses zero and negatives, which the server also refuses', () => {
    expect(tokenCountError('0')).toBe('Must be greater than zero.');
    expect(tokenCountError('-1')).toBe('Must be greater than zero.');
  });

  it('requires a value', () => {
    expect(tokenCountError('')).toBe('A whole number of tokens is required.');
    expect(tokenCountError('   ')).toBe('A whole number of tokens is required.');
  });

  it('accepts a whole number, with surrounding whitespace', () => {
    expect(tokenCountError('400000')).toBeNull();
    expect(tokenCountError(' 128000 ')).toBeNull();
  });
});

describe('priceError', () => {
  it('accepts blank, because unpriced is a real state', () => {
    expect(priceError('')).toBeNull();
    expect(priceError('  ')).toBeNull();
  });

  it('accepts zero, because a free deployment is a real state too', () => {
    expect(priceError('0')).toBeNull();
  });

  it('accepts a fraction — prices are decimal(18,6), unlike token counts', () => {
    expect(priceError('0.075')).toBeNull();
  });

  it('refuses a negative price and unparseable text', () => {
    expect(priceError('-1')).toBe('A price cannot be negative.');
    expect(priceError('$3')).toBe('Enter a price per million tokens, or leave it blank.');
  });
});

describe('toModelFormValue', () => {
  it('seeds an empty form with the server’s own tool default', () => {
    const value = toModelFormValue(null);

    expect(value.providerId).toBe('');
    expect(value.contextWindowSize).toBe('');
    // `IsToolEnabled` defaults to true on the column; a model that cannot call tools is
    // the exception, so the toggle starts where most models end up.
    expect(value.isToolEnabled).toBe(true);
    expect(value.isReasoningEnabled).toBe(false);
    // Also the column's default, and the one that matters: a model created hidden would
    // be saved into nobody's picker.
    expect(value.isUserSelectable).toBe(true);
    expect(value.isDefault).toBe(false);
  });

  it('carries a hidden model through unchanged', () => {
    const value = toModelFormValue(modelFixture({ isUserSelectable: false }));

    expect(value.isUserSelectable).toBe(false);
    expect(toModelBody(value)?.isUserSelectable).toBe(false);
  });

  it('renders an unpriced model as blank fields, never as zero', () => {
    const value = toModelFormValue(
      modelFixture({ inputPricePerMillionTokens: null, outputPricePerMillionTokens: null }),
    );

    expect(value.inputPricePerMillionTokens).toBe('');
    expect(value.outputPricePerMillionTokens).toBe('');
  });

  it('round-trips every field the PUT requires', () => {
    const model = modelFixture({
      contextWindowSize: 200_000,
      maxOutputTokens: 64_000,
      isReasoningEnabled: true,
      inputPricePerMillionTokens: 3,
      outputPricePerMillionTokens: 15,
    });

    expect(toModelFormValue(model)).toEqual({
      providerId: model.providerId,
      name: model.name,
      deploymentName: model.deploymentName,
      description: model.description,
      contextWindowSize: '200000',
      maxOutputTokens: '64000',
      inputPricePerMillionTokens: '3',
      outputPricePerMillionTokens: '15',
      isToolEnabled: model.isToolEnabled,
      isReasoningEnabled: true,
      isUserSelectable: model.isUserSelectable,
      isDefault: model.isDefault,
    });
  });
});

describe('toModelBody', () => {
  it('parses the numbers and trims the text', () => {
    expect(toModelBody(formValue({ name: '  Claude Sonnet 4.5  ' }))).toEqual({
      providerId: PROVIDER_ID.anthropic,
      name: 'Claude Sonnet 4.5',
      deploymentName: 'claude-sonnet-4-5',
      description: 'General-purpose reasoning model.',
      contextWindowSize: 200_000,
      maxOutputTokens: 64_000,
      isToolEnabled: true,
      isReasoningEnabled: true,
      isUserSelectable: true,
      isDefault: false,
      inputPricePerMillionTokens: 3,
      outputPricePerMillionTokens: 15,
    });
  });

  it('sends a blank price as null, never as zero', () => {
    // A report that sums a blank as zero understates spend rather than reporting a gap
    // it could name, which is why the column is nullable in the first place.
    const body = toModelBody(
      formValue({ inputPricePerMillionTokens: '', outputPricePerMillionTokens: ' ' }),
    );

    expect(body?.inputPricePerMillionTokens).toBeNull();
    expect(body?.outputPricePerMillionTokens).toBeNull();
  });

  it('sends a zero price as zero, which is not the same thing', () => {
    expect(
      toModelBody(formValue({ inputPricePerMillionTokens: '0' }))?.inputPricePerMillionTokens,
    ).toBe(0);
  });

  it('refuses rather than sending NaN for an unusable number', () => {
    expect(toModelBody(formValue({ contextWindowSize: '400k' }))).toBeNull();
    expect(toModelBody(formValue({ maxOutputTokens: '0' }))).toBeNull();
    expect(toModelBody(formValue({ inputPricePerMillionTokens: '$3' }))).toBeNull();
  });

  it('refuses a body missing anything the server requires', () => {
    expect(toModelBody(formValue({ providerId: '' }))).toBeNull();
    expect(toModelBody(formValue({ name: '   ' }))).toBeNull();
    expect(toModelBody(formValue({ deploymentName: '' }))).toBeNull();
    expect(toModelBody(formValue({ description: '' }))).toBeNull();
  });

  it('carries the four fields frame `5f` does not draw', () => {
    // `ModelMapper` assigns all four unconditionally, so a body without them clears the
    // stored prices, reads `isReasoningEnabled` as false, and reveals a hidden model.
    const body = toModelBody(formValue());

    expect(body).toHaveProperty('isReasoningEnabled', true);
    expect(body).toHaveProperty('isUserSelectable', true);
    expect(body).toHaveProperty('inputPricePerMillionTokens', 3);
    expect(body).toHaveProperty('outputPricePerMillionTokens', 15);
  });
});

describe('PROVIDER_OPTIONS', () => {
  it('offers all four seeded providers, Azure AI Foundry included', () => {
    // The client-side mirror is the only source: there is no `GET api/providers`.
    expect(PROVIDER_OPTIONS.map((option) => option.name)).toEqual([
      'Azure OpenAI',
      'Azure AI Foundry',
      'Amazon Bedrock',
      'Anthropic',
    ]);
  });

  it('offers display names rather than the seed’s own `nameof` values', () => {
    // The seeded rows are literally `AzureOpenAI`, `AzureAIFoundry`, and so on.
    expect(PROVIDER_OPTIONS.some((option) => option.name === 'AzureAIFoundry')).toBe(false);
  });
});
