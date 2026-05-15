import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { environment } from '../../environments/environment';
import { Observable } from 'rxjs';
import { McpDto } from '../dtos/McpDto';

@Injectable({
  providedIn: 'root',
})
export class McpService {
  private readonly http = inject(HttpClient);

  /**
   * Retrieves a list of MCP servers from the API.
   *
   * @returns An Observable that emits an array of McpDto objects representing the available MCP servers.
   */
  getMcpServers(): Observable<McpDto[]> {
    return this.http.get<McpDto[]>(`${environment.apiUrl}mcps`);
  }
}
