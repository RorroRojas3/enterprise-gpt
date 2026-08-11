/**
 * A model the caller may use, as `GET api/models` returns it.
 *
 * The route returns active models only and is not permission-gated, so this list is
 * the catalog every user sees.
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
  readonly isDefault: boolean;
  /** Only populated by the administrative `GET api/models/all`. */
  readonly dateDeactivated?: string | null;
}
