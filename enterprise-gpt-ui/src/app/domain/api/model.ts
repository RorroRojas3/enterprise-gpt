/**
 * A model the caller may use, as `GET api/models` returns it.
 *
 * The route returns active models only and is not permission-gated, so this list is
 * the catalog every user sees. `GET api/models/all` is Administrator-gated and returns
 * the same shape for retired models too (US-1207).
 *
 * `contextWindowSize` and `maxOutputTokens` are `decimal` server-side and arrive as
 * JSON numbers. `isToolEnabled` is what disables the MCP picker for a turn (FR-9).
 */
export interface ModelDto {
  readonly id: string;
  readonly providerId: string;
  readonly name: string;
  readonly deploymentName: string;
  readonly description: string;
  readonly contextWindowSize: number;
  readonly maxOutputTokens: number;
  readonly isToolEnabled: boolean;
  /** Read only by the Azure AI Foundry chat client; no other provider consults it. */
  readonly isReasoningEnabled: boolean;
  readonly isDefault: boolean;
  /**
   * Null is **unpriced**, not free. A report that sums nulls as zero silently
   * understates spend, which is why the server models them as nullable decimals
   * rather than defaulting them.
   */
  readonly inputPricePerMillionTokens: number | null;
  readonly outputPricePerMillionTokens: number | null;
  /** Only populated by the administrative `GET api/models/all`. */
  readonly dateDeactivated?: string | null;
}

/**
 * The body `POST api/models` and `PUT api/models/{id}` both take.
 *
 * `CreateModelActionDto` and `UpdateModelActionDto` are the same eleven fields
 * server-side, so one shape serves both.
 *
 * **The PUT is a full representation, not a patch.** `ModelMapper` assigns every
 * field unconditionally, so an omitted price *clears* the stored one and an omitted
 * `isReasoningEnabled` reads as `false`. Anything the form does not edit still has to
 * be echoed back — the same rule `UpdateUserActionDto` imposes on the user profile.
 */
export interface ModelWriteBody {
  readonly providerId: string;
  readonly name: string;
  readonly deploymentName: string;
  readonly description: string;
  readonly contextWindowSize: number;
  readonly maxOutputTokens: number;
  readonly isToolEnabled: boolean;
  readonly isReasoningEnabled: boolean;
  readonly isDefault: boolean;
  readonly inputPricePerMillionTokens: number | null;
  readonly outputPricePerMillionTokens: number | null;
}

/** Whether `GET api/models/all` returned this model as retired (US-1207). */
export function isRetired(model: ModelDto): boolean {
  return (model.dateDeactivated ?? null) !== null;
}
