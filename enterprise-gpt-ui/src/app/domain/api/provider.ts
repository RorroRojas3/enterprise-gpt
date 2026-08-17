/**
 * The four providers that exist at deployment time, mirroring
 * `Enterprise.Gpt.Dto.Enums.Providers` — whose ids are documented as matching
 * the seeded `Core.Ref.Provider` rows verbatim, which is what makes a
 * client-side mirror safe.
 *
 * A mirror rather than a fetch because **there is no `GET api/providers`**: no
 * endpoint exposes provider names to any caller, administrator included. This map is
 * how the model picker renders a provider's display name and colour dot (frame `2b`),
 * and how the admin catalog offers the Provider select (frame `5f`, US-1207).
 *
 * The seeded rows spell their names `AzureOpenAI`, `AzureAIFoundry`, `AmazonBedrock`
 * and `Anthropic` — `nameof(...)` values, not display copy — which is the other half
 * of why the labels live here.
 */
export const PROVIDER_ID = {
  azureOpenAi: '3f2a91b5-9e5a-4a0a-a57a-ec70b540bbf0',
  azureAiFoundry: 'b7d4e0c3-5a18-4f92-9c6e-2d31f8a70b45',
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
  // Deliberately shares Azure OpenAI's tone. The boards draw three provider hues and
  // the palette has no fourth; inventing a brand colour is a decision for
  // `docs/design/`, not for this file. The provider *name* renders in the same cell,
  // and the dot groups the two Azure providers — which is the honest reading of it.
  [PROVIDER_ID.azureAiFoundry]: { name: 'Azure AI Foundry', tone: 'provider-azure-openai' },
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
