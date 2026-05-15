import { SafeHtml } from '@angular/platform-browser';

export interface MessageDto {
  content?: string;
  isOutgoing: boolean;
  markdown?: SafeHtml;
}

export function createMessage(
  content: string,
  isOutgoing: boolean,
  markdown?: SafeHtml
): MessageDto {
  return { content, isOutgoing, markdown };
}
