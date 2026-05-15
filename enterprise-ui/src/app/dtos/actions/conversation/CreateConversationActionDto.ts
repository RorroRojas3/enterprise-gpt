export class CreateConversationActionDto {
  projectId?: string;

  constructor(projectId?: string) {
    this.projectId = projectId;
  }
}
