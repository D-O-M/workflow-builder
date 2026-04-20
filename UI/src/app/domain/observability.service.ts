import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RuntimeClientConfigService } from './runtime-client-config.service';

export type ObservabilityWindow = '15m' | '1h' | '6h' | '24h' | '7d';

export interface ProcessObservabilitySummaryViewModel {
  window: ObservabilityWindow | string;
  from: string;
  to: string;
  successfulCount: number;
  failedCount: number;
  totalCount: number;
}

export interface NodeObservabilitySummaryItemViewModel {
  blueprintNodeId: string;
  taskName: string;
  taskDefinitionId: number;
  successfulCount: number;
  failedCount: number;
  totalCount: number;
  successRate: number;
}

export interface NodeObservabilitySummaryViewModel {
  window: ObservabilityWindow | string;
  from: string;
  to: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  nodes: NodeObservabilitySummaryItemViewModel[];
}

export interface RecentProcessViewModel {
  processId: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  status: 'Completed' | 'Failed' | 'InProgress' | 'Pending';
  occurredAt: string;
  failureMessage?: string | null;
}

export interface RecentProcessesViewModel {
  window: ObservabilityWindow | string;
  from: string;
  to: string;
  processes: RecentProcessViewModel[];
}

export interface NodeLogEntryViewModel {
  cursor: string;
  timestamp: string;
  message: string;
  serviceName?: string | null;
  level?: string | null;
}

export interface ProcessStepLogBatchViewModel {
  entries: NodeLogEntryViewModel[];
  nextCursor?: string | null;
}

export interface ProcessLogEntryViewModel {
  cursor: string;
  timestamp: string;
  message: string;
  processStepId?: string | null;
  blueprintNodeId?: string | null;
  taskName?: string | null;
  pluginName?: string | null;
  serviceName?: string | null;
  level?: string | null;
}

export interface ProcessLogBatchViewModel {
  entries: ProcessLogEntryViewModel[];
  nextCursor?: string | null;
}

@Injectable({ providedIn: 'root' })
export class ObservabilityService {
  private readonly http = inject(HttpClient);
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);

  getProcessSummary(window: ObservabilityWindow, blueprintDefinitionId?: number | null): Observable<ProcessObservabilitySummaryViewModel> {
    let params = new HttpParams().set('window', window);
    if (blueprintDefinitionId) {
      params = params.set('blueprintDefinitionId', blueprintDefinitionId);
    }

    return this.http.get<ProcessObservabilitySummaryViewModel>('api/observability/processes/summary', { params });
  }

  getNodeSummary(window: ObservabilityWindow, blueprintDefinitionId: number): Observable<NodeObservabilitySummaryViewModel> {
    const params = new HttpParams()
      .set('window', window)
      .set('blueprintDefinitionId', blueprintDefinitionId);

    return this.http.get<NodeObservabilitySummaryViewModel>('api/observability/nodes/summary', { params });
  }

  getRecentProcesses(window: ObservabilityWindow, blueprintDefinitionId?: number | null, limit = 8): Observable<RecentProcessesViewModel> {
    let params = new HttpParams()
      .set('window', window)
      .set('limit', limit);
    if (blueprintDefinitionId) {
      params = params.set('blueprintDefinitionId', blueprintDefinitionId);
    }

    return this.http.get<RecentProcessesViewModel>('api/observability/processes/recent', { params });
  }

  getProcessStepLogsByRuntimeId(
    processId: string,
    runtimeProcessStepId: string,
    options: {
      from?: string | null;
      to?: string | null;
      limit?: number;
      cursor?: string | null;
    } = {},
  ): Observable<ProcessStepLogBatchViewModel> {
    let params = new HttpParams();
    if (options.from) {
      params = params.set('from', options.from);
    }
    if (options.to) {
      params = params.set('to', options.to);
    }
    if (options.limit) {
      params = params.set('limit', options.limit);
    }
    if (options.cursor) {
      params = params.set('cursor', options.cursor);
    }

    return this.http.get<ProcessStepLogBatchViewModel>(
      `api/observability/processes/${processId}/steps/${runtimeProcessStepId}/logs`,
      { params },
    );
  }

  getProcessStepLogs(
    blueprintDefinitionId: number,
    processId: string,
    blueprintNodeId: string,
    options: {
      from?: string | null;
      to?: string | null;
      limit?: number;
      cursor?: string | null;
    } = {},
  ): Observable<ProcessStepLogBatchViewModel> {
    let params = new HttpParams();
    if (options.from) {
      params = params.set('from', options.from);
    }
    if (options.to) {
      params = params.set('to', options.to);
    }
    if (options.limit) {
      params = params.set('limit', options.limit);
    }
    if (options.cursor) {
      params = params.set('cursor', options.cursor);
    }

    return this.http.get<ProcessStepLogBatchViewModel>(
      `api/blueprints/${blueprintDefinitionId}/processes/${processId}/steps/${blueprintNodeId}/logs`,
      { params },
    );
  }

  createProcessStepLogStreamByRuntimeId(
    processId: string,
    runtimeProcessStepId: string,
    options: {
      from?: string | null;
      to?: string | null;
      cursor?: string | null;
    } = {},
  ): EventSource {
    const url = new URL(
      `${this.runtimeClientConfig.apiBaseUrl}/api/observability/processes/${processId}/steps/${runtimeProcessStepId}/logs/stream`,
    );

    if (options.from) {
      url.searchParams.set('from', options.from);
    }
    if (options.to) {
      url.searchParams.set('to', options.to);
    }
    if (options.cursor) {
      url.searchParams.set('cursor', options.cursor);
    }

    return new EventSource(url.toString(), { withCredentials: false });
  }

  createProcessStepLogStream(
    blueprintDefinitionId: number,
    processId: string,
    blueprintNodeId: string,
    options: {
      from?: string | null;
      to?: string | null;
      cursor?: string | null;
    } = {},
  ): EventSource {
    const url = new URL(
      `${this.runtimeClientConfig.apiBaseUrl}/api/blueprints/${blueprintDefinitionId}/processes/${processId}/steps/${blueprintNodeId}/logs/stream`,
    );

    if (options.from) {
      url.searchParams.set('from', options.from);
    }
    if (options.to) {
      url.searchParams.set('to', options.to);
    }
    if (options.cursor) {
      url.searchParams.set('cursor', options.cursor);
    }

    return new EventSource(url.toString(), { withCredentials: false });
  }

  getProcessLogs(
    processId: string,
    options: {
      from?: string | null;
      to?: string | null;
      limit?: number;
      cursor?: string | null;
    } = {},
  ): Observable<ProcessLogBatchViewModel> {
    let params = new HttpParams();
    if (options.from) {
      params = params.set('from', options.from);
    }
    if (options.to) {
      params = params.set('to', options.to);
    }
    if (options.limit) {
      params = params.set('limit', options.limit);
    }
    if (options.cursor) {
      params = params.set('cursor', options.cursor);
    }

    return this.http.get<ProcessLogBatchViewModel>(
      `api/observability/processes/${processId}/logs`,
      { params },
    );
  }

  createProcessLogStream(
    processId: string,
    options: {
      from?: string | null;
      to?: string | null;
      cursor?: string | null;
    } = {},
  ): EventSource {
    const url = new URL(
      `${this.runtimeClientConfig.apiBaseUrl}/api/observability/processes/${processId}/logs/stream`,
    );

    if (options.from) {
      url.searchParams.set('from', options.from);
    }
    if (options.to) {
      url.searchParams.set('to', options.to);
    }
    if (options.cursor) {
      url.searchParams.set('cursor', options.cursor);
    }

    return new EventSource(url.toString(), { withCredentials: false });
  }
}
