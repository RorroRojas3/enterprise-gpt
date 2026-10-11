/**
 * Mirrors `Enterprise.Gpt.Dto.Enums.Providers`, whose ids match the seeded rows. A client-side
 * mirror because no endpoint exposes provider names, and the seeded ones are `nameof` values.
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
  // Shares Azure OpenAI's tone: the palette has three provider hues, and the dot groups the
  // two Azure providers.
  [PROVIDER_ID.azureAiFoundry]: { name: 'Azure AI Foundry', tone: 'provider-azure-openai' },
  [PROVIDER_ID.amazonBedrock]: { name: 'Amazon Bedrock', tone: 'provider-bedrock' },
  [PROVIDER_ID.anthropic]: { name: 'Anthropic', tone: 'provider-anthropic' },
};

/** Null for an unknown id: a newly seeded provider must degrade, not break the picker. */
export function providerMeta(providerId: string): ProviderMeta | null {
  return PROVIDERS[providerId] ?? null;
}
