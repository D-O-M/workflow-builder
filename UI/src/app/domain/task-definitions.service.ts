import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

@Injectable({ providedIn: 'root' })
export class TaskDefinitionsService {
  private readonly http = inject(HttpClient);

  getTaskDefinitions(): Observable<ITaskDefinition[]> {
    return this.http.get<ITaskDefinition[]>('tasks');
  }
}
