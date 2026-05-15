export interface ConversationDto {
  id: string;
  name: string;
  projectId?: string;
  dateCreated: string; // ISO date string from API
  dateModified: string; // ISO date string from API
}

export interface ConversationMessageDto {
  id: string;
  name: string;
  dateCreated: string;
  dateModified: string;
  text: string;
  role: number;
}

export interface ChatConversationDto {
  id: string;
  name: string;
  dateCreated: string;
  dateModified: string;
  messages: ConversationMessageDto[];
}
