import { ModelDto, ModelWriteBody } from '@shared/models/api/chat-model.model';
import { PROVIDER_ID } from '@shared/models/api/provider.model';
import { providerMeta } from './provider-meta';

/** Mirrors `CreateModelActionDtoValidator`, so a rejection is the exception. */
export const MODEL_FIELD_LENGTHS = {
  name: 256,
  deploymentName: 512,
  description: 1024,
} as const;

export interface ProviderOption {
  readonly id: string;
  readonly name: string;
}

/**
 * Kept out of `provider.model.ts` so it stays off the initial graph. With no
 * `GET api/providers`, an id a deployment has retired surfaces only as the save's 404.
 */
export const PROVIDER_OPTIONS: readonly ProviderOption[] = Object.values(PROVIDER_ID).map((id) => ({
  id,
  name: providerMeta(id)?.name ?? id,
}));

/**
 * Numeric fields are strings: `type="number"` yields null for unparsable text, losing what was
 * typed and conflating "blank" (unpriced) with "not a number".
 */
export interface ModelFormValue {
  readonly providerId: string;
  readonly name: string;
  readonly deploymentName: string;
  readonly description: string;
  readonly contextWindowSize: string;
  readonly maxOutputTokens: string;
  readonly inputPricePerMillionTokens: string;
  readonly outputPricePerMillionTokens: string;
  readonly isToolEnabled: boolean;
  readonly isReasoningEnabled: boolean;
  readonly isUserSelectable: boolean;
  readonly isDefault: boolean;
}

export const WHOLE_TOKENS_MESSAGE = 'Must be a whole number of tokens.';

/**
 * The whole-number rule is the client's own: the server's `decimal(18,2)` columns only check
 * `GreaterThan(0)`, so it would store `400000.5`.
 */
export function tokenCountError(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return 'A whole number of tokens is required.';
  }

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || !Number.isInteger(parsed)) {
    return WHOLE_TOKENS_MESSAGE;
  }

  return parsed > 0 ? null : 'Must be greater than zero.';
}

/** Blank means unpriced and zero means free, so blank cannot be normalised to zero. */
export function priceError(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed)) {
    return 'Enter a price per million tokens, or leave it blank.';
  }

  return parsed >= 0 ? null : 'A price cannot be negative.';
}

export function toModelFormValue(model: ModelDto | null): ModelFormValue {
  if (model === null) {
    return {
      providerId: '',
      name: '',
      deploymentName: '',
      description: '',
      contextWindowSize: '',
      maxOutputTokens: '',
      inputPricePerMillionTokens: '',
      outputPricePerMillionTokens: '',
      // The server's column default; most models can call tools.
      isToolEnabled: true,
      // Also the column default: a model created hidden would vanish from the picker on save.
      isUserSelectable: true,
      isReasoningEnabled: false,
      isDefault: false,
    };
  }

  return {
    providerId: model.providerId,
    name: model.name,
    deploymentName: model.deploymentName,
    description: model.description,
    contextWindowSize: String(model.contextWindowSize),
    maxOutputTokens: String(model.maxOutputTokens),
    inputPricePerMillionTokens: priceText(model.inputPricePerMillionTokens),
    outputPricePerMillionTokens: priceText(model.outputPricePerMillionTokens),
    isToolEnabled: model.isToolEnabled,
    isReasoningEnabled: model.isReasoningEnabled,
    isUserSelectable: model.isUserSelectable,
    isDefault: model.isDefault,
  };
}

/**
 * Shared by both verbs: `ModelMapper` assigns every property on a PUT, so a missing price clears
 * the stored one and a missing `isReasoningEnabled` reads as `false`.
 */
export function toModelBody(value: ModelFormValue): ModelWriteBody | null {
  if (tokenCountError(value.contextWindowSize) !== null) {
    return null;
  }

  if (tokenCountError(value.maxOutputTokens) !== null) {
    return null;
  }

  if (
    priceError(value.inputPricePerMillionTokens) !== null ||
    priceError(value.outputPricePerMillionTokens) !== null
  ) {
    return null;
  }

  const providerId = value.providerId.trim();
  const name = value.name.trim();
  const deploymentName = value.deploymentName.trim();
  const description = value.description.trim();

  if (providerId === '' || name === '' || deploymentName === '' || description === '') {
    return null;
  }

  return {
    providerId,
    name,
    deploymentName,
    description,
    contextWindowSize: Number(value.contextWindowSize.trim()),
    maxOutputTokens: Number(value.maxOutputTokens.trim()),
    isToolEnabled: value.isToolEnabled,
    isReasoningEnabled: value.isReasoningEnabled,
    isUserSelectable: value.isUserSelectable,
    isDefault: value.isDefault,
    inputPricePerMillionTokens: toPrice(value.inputPricePerMillionTokens),
    outputPricePerMillionTokens: toPrice(value.outputPricePerMillionTokens),
  };
}

/** Blank is unpriced, not free — the column is nullable and zero is a real price. */
function toPrice(raw: string): number | null {
  const trimmed = raw.trim();

  return trimmed === '' ? null : Number(trimmed);
}

function priceText(price: number | null): string {
  return price === null ? '' : String(price);
}
