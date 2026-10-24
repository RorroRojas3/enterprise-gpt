import { ModelDto } from '@core/api/chat-model.model';

export function isRetired(model: ModelDto): boolean {
  return (model.dateDeactivated ?? null) !== null;
}
