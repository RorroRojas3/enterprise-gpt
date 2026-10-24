import { ModelDto } from '@shared/models/api/chat-model.model';

export function isRetired(model: ModelDto): boolean {
  return (model.dateDeactivated ?? null) !== null;
}
