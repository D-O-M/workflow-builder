import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

export interface CapabilitySetting {
  key: string;
  capabilityId: string;
  capabilityName: string;
  kind: string;
  description: string | null;
  required: boolean;
  secret: boolean;
  overridden: boolean;
  value: string | null;
}

export interface ManagedVersion {
  id: number;
  version: string;
  lane: string;
  enabled: boolean;
  effectiveEnabled: boolean;
  runnable: boolean;
  serving: boolean;
  status: string;
}

export interface ManagedPlugin {
  id: number;
  name: string;
  displayName: string | null;
  enabled: boolean;
  versions: ManagedVersion[];
}

export interface Fleet {
  writesEnabled: boolean;
  revision: number;
  status: string;
  engines: { engineId: string; appliedRevision: number | null; lastSeenAt: string; failureCode: string | null }[];
}

@Injectable({ providedIn: 'root' })
export class ManagementApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/engine-management';

  settings() {
    return this.http.get<{ revision: number; settings: CapabilitySetting[] }>(this.base + '/settings');
  }
  plugins() {
    return this.http.get<{ revision: number; plugins: ManagedPlugin[] }>(this.base + '/plugins');
  }
  fleet() {
    return this.http.get<Fleet>(this.base + '/fleet');
  }
  save(key: string, value: string, expectedRevision: number) {
    return this.http.put<{ revision: number }>(this.base + '/settings/' + encodeURIComponent(key), { value, expectedRevision });
  }
  remove(key: string, expectedRevision: number) {
    return this.http.delete<{ revision: number }>(this.base + '/settings/' + encodeURIComponent(key), { params: { expectedRevision } });
  }
  enable(plugin: ManagedPlugin, version: ManagedVersion | null, enabled: boolean, expectedRevision: number) {
    const target = this.base + '/plugins/' + plugin.id + (version ? '/versions/' + version.id : '');
    return this.http.put<{ revision: number }>(target, { enabled, expectedRevision });
  }
}

export function managementError(error: unknown): string {
  const status = error instanceof HttpErrorResponse ? error.status : 0;
  switch (status) {
    case 400: return 'Check the value against the declared setting type and try again.';
    case 401: return 'Sign in with a runtime engineer or administrator account.';
    case 403: return 'Engine management requires the runtime engineer or administrator role.';
    case 404: return 'Engine management or the selected item is unavailable in this deployment.';
    case 409: return 'Another operator saved a change. Review the refreshed values before trying again.';
    case 503: return 'The management service is temporarily unavailable. Refresh before trying again.';
    default: return 'Could not reach engine management. Check the connection and refresh.';
  }
}
