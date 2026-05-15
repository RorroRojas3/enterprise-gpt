export class UpdateConversationActionDto {
  id!: string;
  name!: string;
  projectId?: string;

  constructor(conversationId?: string, conversationName?: string, projectId?: string) {
    this.id = conversationId!;
    this.name = conversationName!;
    this.projectId = projectId;
  }
}
