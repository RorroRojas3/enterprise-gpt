import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { UserDto } from '../dtos/UserDto';

@Injectable({
  providedIn: 'root',
})
export class UserService {
  private readonly http = inject(HttpClient);

  /**
   * Upserts the authenticated user on the API. The backend creates the user
   * from Microsoft Graph data on first call (returns 201) and returns the
   * existing record on subsequent calls (returns 200). Both paths return the
   * full `UserDto`, including granted permissions.
   *
   * @returns The authenticated user, including any active permissions.
   */
  createUser(): Observable<UserDto> {
    return this.http.post<UserDto>(`${environment.apiUrl}users/me`, {});
  }
}
