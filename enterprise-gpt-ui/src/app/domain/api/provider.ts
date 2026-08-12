/**
 * The three providers that exist at deployment time, mirroring
 * `Enterprise.Gpt.Dto.Enums.Providers` — whose ids are documented as matching
 * the seeded `Core.Ref.Provider` rows verbatim, which is what makes a
 * client-side mirror safe.
 *
 * `ModelDto` carries only `providerId`; no endpoint exposes provider names to
 * a non-admin caller. This map is how the model picker renders a provider's
 * display name and colour dot (frame `2b`).
 */
export const PROVIDER_ID = {
  azureOpenAi: '3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0',
  amazonBedrock: '8c4b1f27-6a3d-4d59-9e21-b0f4a7c6d812',
  anthropic: '6f93ec17-e981-409f-a523-700584f1e7d6',
} as const;

/** The status-dot tone a provider renders with. */
export type ProviderTone = 'provider-azure-openai' | 'provider-bedrock' | 'provider-anthropic';

/** How a provider is presented in the model picker. */
export interface ProviderMeta {
  readonly name: string;
  readonly tone: ProviderTone;
}

const PROVIDERS: Readonly<Record<string, ProviderMeta>> = {
  [PROVIDER_ID.azureOpenAi]: { name: 'Azure OpenAI', tone: 'provider-azure-openai' },
  [PROVIDER_ID.amazonBedrock]: { name: 'Amazon Bedrock', tone: 'provider-bedrock' },
  [PROVIDER_ID.anthropic]: { name: 'Anthropic', tone: 'provider-anthropic' },
};

/**
 * Resolves a provider's presentation, or null for an id this build does not
 * know — a new provider seeded server-side must degrade to a muted dot and a
 * caption without the provider segment, not break the picker.
 */
export function providerMeta(providerId: string): ProviderMeta | null {
  return PROVIDERS[providerId] ?? null;
}
