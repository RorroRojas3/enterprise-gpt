import { MCP_AUTH_TYPE, McpDto, McpServerDto } from '../app/domain/api/mcp';
import { ModelDto } from '../app/domain/api/model';
import { PROVIDER_ID } from '../app/domain/api/provider';

let modelSequence = 0;

/** A `ModelDto` exactly as `GET api/models` returns one. */
export function modelFixture(overrides: Partial<ModelDto> = {}): ModelDto {
  const index = modelSequence++;

  return {
    // No modulo — a repeating id collapses list joins silently. See conversations.ts.
    id: `${String(index).padStart(8, '0')}-aaaa-4bbb-8ccc-dddddddddddd`,
    providerId: PROVIDER_ID.azureOpenAi,
    name: `Model ${index}`,
    deploymentName: `model-${index}`,
    description: `Test model ${index}.`,
    contextWindowSize: 400_000,
    maxOutputTokens: 16_384,
    isToolEnabled: true,
    isReasoningEnabled: false,
    isDefault: false,
    // Unpriced by default, which is what a model created before US-1207 looks like.
    inputPricePerMillionTokens: null,
    outputPricePerMillionTokens: null,
    ...overrides,
  };
}

let mcpSequence = 0;

/** An `McpDto` exactly as `GET api/mcps` returns one. */
export function mcpFixture(overrides: Partial<McpDto> = {}): McpDto {
  const index = mcpSequence++;

  return {
    id: `${String(index).padStart(8, '0')}-1111-4222-8333-555555555555`,
    name: `Tool Server ${index}`,
    description: null,
    ...overrides,
  };
}

let mcpServerSequence = 0;

/** An `McpServerDto` exactly as the administrative `GET api/mcps/all` returns one. */
export function mcpServerFixture(overrides: Partial<McpServerDto> = {}): McpServerDto {
  const index = mcpServerSequence++;

  return {
    id: `${String(index).padStart(8, '0')}-2222-4333-8444-666666666666`,
    name: `MCP Server ${index}`,
    // Non-null here and nullable on `McpDto`: the administrative DTO declares it
    // required, and its validator refuses an empty one.
    description: `Test MCP server ${index}.`,
    url: `https://mcp.example.test/server-${index}`,
    // A server with no authentication, which is what Microsoft Learn and Context7 are —
    // and the only arm that must carry a null scope.
    authType: MCP_AUTH_TYPE.none,
    scope: null,
    permissionId: `${String(index).padStart(8, '0')}-3333-4444-8555-777777777777`,
    ...overrides,
  };
}
