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
}
