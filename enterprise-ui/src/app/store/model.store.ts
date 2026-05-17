import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { ModelDto } from '../dtos/ModelDto';

type ModelState = {
  models: ModelDto[];
  selectedModel: ModelDto | null;
};

const initialState: ModelState = {
  models: [],
  selectedModel: null,
};

export const ModelStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store) => ({
    /**
     * Sets the list of available models and selects the user's favorite
     * (falling back to the first model when none is flagged).
     *
     * @param models - Array of ModelDto objects to set as available models
     */
    setModels(models: ModelDto[]): void {
      patchState(store, {
        models,
        selectedModel: models.find((m) => m.isFavorite) ?? models[0] ?? null,
      });
    },

    /**
     * Sets the currently selected model.
     *
     * @param model - The ModelDto to set as the selected model
     */
    setSelectedModel(model: ModelDto): void {
      patchState(store, { selectedModel: model });
    },
  })),
);
