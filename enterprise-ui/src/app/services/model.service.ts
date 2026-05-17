import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ModelDto } from '../dtos/ModelDto';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root',
})
export class ModelService {
  private readonly http = inject(HttpClient);

  /**
   * Retrieves the list of available models from the API.
   *
   * @returns {Observable<ModelDto[]>} An observable that emits an array of ModelDto objects.
   */
  getModels(): Observable<ModelDto[]> {
    return this.http.get<ModelDto[]>(`${environment.apiUrl}models`);
  }

  /**
   * Retrieves the available models with the current user's favorite first.
   *
   * @returns {Observable<ModelDto[]>} An observable that emits the models, favorite first.
   */
  getFavoriteModels(): Observable<ModelDto[]> {
    return this.http.get<ModelDto[]>(`${environment.apiUrl}models/me`);
  }

  /**
   * Persists the current user's favorite model (the last one picked).
   *
   * @param modelId - The id of the model to set as favorite.
   * @returns {Observable<ModelDto[]>} An observable that emits the models, favorite first.
   */
  setFavoriteModel(modelId: string): Observable<ModelDto[]> {
    return this.http.put<ModelDto[]>(`${environment.apiUrl}models/me`, {
      modelId,
    });
  }
}
