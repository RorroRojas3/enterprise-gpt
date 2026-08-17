import { ModelDto, ModelWriteBody } from '@domain/api/model';
import { PROVIDER_ID, providerMeta } from '@domain/api/provider';

/** Mirrors `CreateModelActionDtoValidator`, so a rejection is the exception. */
export const MODEL_FIELD_LENGTHS = {
  name: 256,
  deploymentName: 512,
  description: 1024,
} as const;

/** One entry of the model form's Provider select (frame `5f`). */
export interface ProviderOption {
  readonly id: string;
  readonly name: string;
}

/**
 * The providers a model may be assigned to, in the order the seed defines them.
 *
 * Built here rather than beside `providerMeta` so it stays out of the initial graph:
 * `domain/api/provider.ts` is reached by the chat composer's model menu, and this list
 * has exactly one consumer, on a lazy admin route.
 *
 * `ModelService.EnsureProviderExistsAsync` answers 404 for a provider that is missing
 * *or deactivated*, so an id offered here that a deployment has retired fails the save
 * rather than writing silently — the correct failure, and the only one a client with no
 * `GET api/providers` can know about.
 */
export const PROVIDER_OPTIONS: readonly ProviderOption[] = Object.values(PROVIDER_ID).map((id) => ({
  id,
  name: providerMeta(id)?.name ?? id,
}));

/**
 * The values the model form collects (frame `5f`).
 *
 * **The four numeric fields are strings, deliberately.** `<input type="number">` hands
 * back `null` for anything the browser cannot parse, which loses the text the reader
 * typed — and frame `5f` draws `400000.5` still sitting in the field beneath its
 * refusal. Keeping the raw text is also the only way to tell "left blank" (a price the
 * deployment has not set) apart from "typed something that is not a number", which the
 * wire distinguishes and a coerced `null` does not.
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
  readonly isDefault: boolean;
}

/** The message frame `5f` words for a token count that is not a whole number. */
export const WHOLE_TOKENS_MESSAGE = 'Must be a whole number of tokens.';

/**
 * Validates a token count, returning the reason it is unusable or null when it is fine.
 *
 * **The whole-number rule is the client's own.** `ContextWindowSize` and
 * `MaxOutputTokens` are `decimal(18,2)` columns and their validator carries only
 * `GreaterThan(0)`, so the server would accept `400000.5` and store it. Frame `5f` draws
 * the refusal, so it has to live somewhere, and this is the only place it can.
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

/**
 * Validates an optional price, returning the reason it is unusable or null.
 *
 * Blank is valid and means **unpriced**. Zero is also valid — a genuinely free
 * deployment — which is why blank cannot be normalised to it.
 */
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

/** Seeds the form from a model, or from nothing when one is being created. */
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
      // The server's own column default. A model that cannot call tools is the
      // exception, so the toggle starts where most models end up.
      isToolEnabled: true,
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
    isDefault: model.isDefault,
  };
}

/**
 * Builds the request body, or null when a field the wire needs is unusable.
 *
 * Both verbs go through here, so neither can drop a field on a full-representation PUT:
 * `ModelMapper` assigns every property unconditionally, so a body missing a price
 * **clears** the stored one and a body missing `isReasoningEnabled` reads as `false`.
 * The form binds all eleven precisely so that this function never has to guess at one.
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
