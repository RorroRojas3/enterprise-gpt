/**
 * `GET api/models` returns the active catalog every user sees; the admin-only
 * `GET api/models/all` adds retired models in the same shape. `isToolEnabled` gates the MCP
 * picker.
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
  /**
   * Picker visibility, not authorization: a hidden model is still served by id. Always `true`
   * on `GET api/models`; it keeps the pinned summarizer out of the picker without retiring it.
   */
  readonly isUserSelectable: boolean;
  readonly isDefault: boolean;
  /** Null is unpriced, not free; summing nulls as zero would understate spend. */
  readonly inputPricePerMillionTokens: number | null;
  readonly outputPricePerMillionTokens: number | null;
  /** Only populated by the administrative `GET api/models/all`. */
  readonly dateDeactivated?: string | null;
}

/**
 * The body of both `POST api/models` and `PUT api/models/{id}`. The PUT is a full
 * representation: an omitted price clears the stored one, so echo back every unedited field.
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
  readonly isUserSelectable: boolean;
  readonly isDefault: boolean;
  readonly inputPricePerMillionTokens: number | null;
  readonly outputPricePerMillionTokens: number | null;
}

export function isRetired(model: ModelDto): boolean {
  return (model.dateDeactivated ?? null) !== null;
}
