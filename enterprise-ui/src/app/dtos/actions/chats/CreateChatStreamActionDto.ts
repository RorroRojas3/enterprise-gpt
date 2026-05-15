import { McpDto } from '../../McpDto';

export class CreateChatStreamActionDto {
  prompt!: string;
  modelId!: string;
  serviceId!: string;
  mcpServers?: McpDto[];

  constructor(
    prompt: string,
    modelId: string,
    serviceId: string,
    mcpServers?: McpDto[]
  ) {
    this.prompt = prompt;
    this.modelId = modelId;
    this.serviceId = serviceId;
    this.mcpServers = mcpServers;
  }
}
