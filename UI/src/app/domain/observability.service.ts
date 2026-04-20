import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RuntimeClientConfigService } from './runtime-client-config.service';

export type ObservabilityWindow = '15m' | '1h' | '6h' | '24h' | '7d';

export interface WorkflowObservabilitySummaryViewModel {
  window: ObservabilityWindow | string;
  from: string;
  to: string;
  successfulCount: number;
  failedCount: number;
  totalCount: number;
}

export interface NodeObservabilitySummaryItemViewModel {
  blueprintTaskId: string;
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
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  blueprintVersionName: string;
  nodes: NodeObservabilitySummaryItemViewModel[];
}

export interface RecentWorkflowExecutionViewModel {
  workflowInstanceId: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  blueprintVersionName: string;
  status: 'Completed' | 'Failed' | 'InProgress' | 'Pending';
  occurredAt: string;
  failureMessage?: string | null;
}

export interface WorkflowRecentRunsViewModel {
  window: ObservabilityWindow | string;
  from: string;
  to: string;
  runs: RecentWorkflowExecutionViewModel[];
}

export interface NodeLogEntryViewModel {
  cursor: string;
  timestamp: string;
  message: string;
  serviceName?: string | null;
  level?: string | null;
}

export interface NodeLogBatchViewModel {
  entries: NodeLogEntryViewModel[];
  nextCursor?: string | null;
}

export interface WorkflowLogEntryViewModel {
  cursor: string;
  timestamp: string;
  message: string;
  workflowTaskInstanceId?: string | null;
  blueprintTaskId?: string | null;
  taskName?: string | null;
  pluginName?: string | null;
  serviceName?: string | null;
  level?: string | null;
}

export interface WorkflowLogBatchViewModel {
  entries: WorkflowLogEntryViewModel[];
  nextCursor?: string | null;
}

@Injectable({ providedIn: 'root' })
export class ObservabilityService {
  private readonly http = inject(HttpClient);
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);

  getWorkflowSummary(window: ObservabilityWindow, blueprintDefinitionId?: number | null): Observable<WorkflowObservabilitySummaryViewModel> {
    let params = new HttpParams().set('window', window);
    if (blueprintDefinitionId) {
      params = params.set('blueprintDefinitionId', blueprintDefinitionId);
    }

    return this.http.get<WorkflowObservabilitySummaryViewModel>('api/observability/workflows/summary', { params });
  }

  getNodeSummary(window: ObservabilityWindow, blueprintDefinitionId: number): Observable<NodeObservabilitySummaryViewModel> {
    const params = new HttpParams()
      .set('window', window)
      .set('blueprintDefinitionId', blueprintDefinitionId);

    return this.http.get<NodeObservabilitySummaryViewModel>('api/observability/nodes/summary', { params });
  }

  getRecentRuns(window: ObservabilityWindow, blueprintDefinitionId?: number | null, limit = 8): Observable<WorkflowRecentRunsViewModel> {
    let params = new HttpParams()
      .set('window', window)
      .set('limit', limit);
    if (blueprintDefinitionId) {
      params = params.set('blueprintDefinitionId', blueprintDefinitionId);
    }

    return this.http.get<WorkflowRecentRunsViewModel>('api/observability/workflows/recent', { params });
  }

  getNodeLogs(
    workflowInstanceId: string,
    runtimeTaskInstanceId: string,
    options: {
      from?: string | null;
      to?: string | null;
      limit?: number;
      cursor?: string | null;
    } = {},
  ): Observable<NodeLogBatchViewModel> {
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

    return this.http.get<NodeLogBatchViewModel>(
      `api/observability/workflows/${workflowInstanceId}/nodes/${runtimeTaskInstanceId}/logs`,
      { params },
    );
  }

  getExecutionStepLogs(
    blueprintDefinitionId: number,
    workflowInstanceId: string,
    blueprintTaskInstanceId: string,
    options: {
      from?: string | null;
      to?: string | null;
      limit?: number;
      cursor?: string | null;
    } = {},
  ): Observable<NodeLogBatchViewModel> {
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

    return this.http.get<NodeLogBatchViewModel>(
      `api/blueprints/${blueprintDefinitionId}/executions/${workflowInstanceId}/steps/${blueprintTaskInstanceId}/logs`,
      { params },
    );
  }

  createNodeLogStream(
    workflowInstanceId: string,
    runtimeTaskInstanceId: string,
    options: {
      from?: string | null;
      to?: string | null;
      cursor?: string | null;
    } = {},
  ): EventSource {
    const url = new URL(
      `${this.runtimeClientConfig.apiBaseUrl}/api/observability/workflows/${workflowInstanceId}/nodes/${runtimeTaskInstanceId}/logs/stream`,
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

  createExecutionStepLogStream(
    blueprintDefinitionId: number,
    workflowInstanceId: string,
    blueprintTaskInstanceId: string,
    options: {
      from?: string | null;
      to?: string | null;
      cursor?: string | null;
    } = {},
  ): EventSource {
    const url = new URL(
      `${this.runtimeClientConfig.apiBaseUrl}/api/blueprints/${blueprintDefinitionId}/executions/${workflowInstanceId}/steps/${blueprintTaskInstanceId}/logs/stream`,
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

  getWorkflowLogs(
    workflowInstanceId: string,
    options: {
      from?: string | null;
      to?: string | null;
      limit?: number;
      cursor?: string | null;
    } = {},
  ): Observable<WorkflowLogBatchViewModel> {
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

    return this.http.get<WorkflowLogBatchViewModel>(
      `api/observability/workflows/${workflowInstanceId}/logs`,
      { params },
    );
  }

  createWorkflowLogStream(
    workflowInstanceId: string,
    options: {
      from?: string | null;
      to?: string | null;
      cursor?: string | null;
    } = {},
  ): EventSource {
    const url = new URL(
      `${this.runtimeClientConfig.apiBaseUrl}/api/observability/workflows/${workflowInstanceId}/logs/stream`,
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
