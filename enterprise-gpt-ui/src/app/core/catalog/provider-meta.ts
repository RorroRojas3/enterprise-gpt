import { PROVIDER_ID, ProviderMeta } from '@core/api/provider.model';

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
