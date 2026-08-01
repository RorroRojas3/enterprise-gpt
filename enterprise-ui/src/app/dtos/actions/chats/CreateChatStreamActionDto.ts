/**
 * Identifies an MCP server selected for a chat request. Only the id is sent — the
 * backend resolves the rest and enforces permissions.
 */
export interface McpServerSelectionDto {
  id: string;
}

export class CreateChatStreamActionDto {
  prompt!: string;
  modelId!: string;
  serviceId!: string;
  mcpServers?: McpServerSelectionDto[];

  constructor(
    prompt: string,
    modelId: string,
    serviceId: string,
    mcpServers?: McpServerSelectionDto[]
  ) {
    this.prompt = prompt;
    this.modelId = modelId;
    this.serviceId = serviceId;
    this.mcpServers = mcpServers;
  }
}
