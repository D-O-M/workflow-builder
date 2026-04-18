import { Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ExecutionSummaryUpdatedEvent {
  workflowInstanceId: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  blueprintVersionName: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
  completedTaskCount: number;
  failedTaskCount: number;
  inProgressTaskCount: number;
  pendingTaskCount: number;
}

export interface ExecutionNodeDelta {
  instanceId: string;
  taskInstanceId: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  resolvedVersionId: number;
  resolvedVersion?: string | null;
  failureMessage?: string | null;
  updatedAt?: string | null;
}

export interface ExecutionNodesUpdatedEvent {
  workflowInstanceId: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number;
  nodes: ExecutionNodeDelta[];
}

@Injectable({ providedIn: 'root' })
export class ExecutionRealtimeService {
  private connection: HubConnection | null = null;
  private startPromise: Promise<void> | null = null;
  private reconnectTimeoutId: number | null = null;
  private flushHandle: number | null = null;

  private desiredConnected = false;
  private desiredBlueprintId: number | null = null;
  private desiredWorkflowInstanceId: string | null = null;
  private currentBlueprintId: number | null = null;
  private currentWorkflowInstanceId: string | null = null;

  private readonly pendingSummaries: ExecutionSummaryUpdatedEvent[] = [];
  private readonly pendingNodeUpdates: ExecutionNodesUpdatedEvent[] = [];

  private readonly summaryUpdatesSubject = new Subject<ExecutionSummaryUpdatedEvent[]>();
  private readonly nodeUpdatesSubject = new Subject<ExecutionNodesUpdatedEvent[]>();
  private readonly reconnectedSubject = new Subject<void>();

  readonly summaryUpdates$ = this.summaryUpdatesSubject.asObservable();
  readonly nodeUpdates$ = this.nodeUpdatesSubject.asObservable();
  readonly reconnected$ = this.reconnectedSubject.asObservable();

  connect(): void {
    this.desiredConnected = true;
    void this.ensureConnection();
  }

  async disconnect(): Promise<void> {
    this.desiredConnected = false;
    this.desiredBlueprintId = null;
    this.desiredWorkflowInstanceId = null;
    this.currentBlueprintId = null;
    this.currentWorkflowInstanceId = null;
    this.clearReconnectTimeout();
    this.clearPendingEvents();

    const connection = this.connection;
    this.connection = null;
    this.startPromise = null;

    if (!connection) {
      return;
    }

    try {
      await connection.stop();
    } catch (error) {
      console.warn('Failed to stop execution SignalR connection.', error);
    }
  }

  setSubscriptions(blueprintDefinitionId: number | null, workflowInstanceId: string | null): void {
    this.desiredBlueprintId = blueprintDefinitionId;
    this.desiredWorkflowInstanceId = workflowInstanceId;
    void this.applyDesiredSubscriptions();
  }

  private async ensureConnection(): Promise<void> {
    if (!this.desiredConnected) {
      return;
    }

    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) {
      return;
    }

    if (this.startPromise) {
      await this.startPromise;
      return;
    }

    const connection = this.connection ?? this.createConnection();
    this.connection = connection;

    this.startPromise = connection.start()
      .then(async () => {
        this.clearReconnectTimeout();
        await this.applyDesiredSubscriptions();
      })
      .catch((error) => {
        console.warn('Failed to start execution SignalR connection.', error);
        this.scheduleReconnect();
      })
      .finally(() => {
        this.startPromise = null;
      });

    await this.startPromise;
  }

  private async applyDesiredSubscriptions(): Promise<void> {
    if (!this.desiredConnected) {
      return;
    }

    if (!this.connection || this.connection.state === HubConnectionState.Disconnected) {
      await this.ensureConnection();
      return;
    }

    if (this.connection.state !== HubConnectionState.Connected) {
      return;
    }

    if (this.currentBlueprintId !== null && this.currentBlueprintId !== this.desiredBlueprintId) {
      await this.connection.invoke('UnsubscribeBlueprint', this.currentBlueprintId);
      this.currentBlueprintId = null;
    }

    if (this.currentWorkflowInstanceId !== null &&
      this.currentWorkflowInstanceId !== this.desiredWorkflowInstanceId) {
      await this.connection.invoke('UnsubscribeWorkflow', this.currentWorkflowInstanceId);
      this.currentWorkflowInstanceId = null;
    }

    if (this.desiredBlueprintId !== null && this.currentBlueprintId !== this.desiredBlueprintId) {
      await this.connection.invoke('SubscribeBlueprint', this.desiredBlueprintId);
      this.currentBlueprintId = this.desiredBlueprintId;
    }

    if (this.desiredWorkflowInstanceId !== null &&
      this.currentWorkflowInstanceId !== this.desiredWorkflowInstanceId) {
      await this.connection.invoke('SubscribeWorkflow', this.desiredWorkflowInstanceId);
      this.currentWorkflowInstanceId = this.desiredWorkflowInstanceId;
    }
  }

  private createConnection(): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl(`${environment.signalRBaseUrl}/hubs/executions`, {
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('executionSummaryUpdated', (event: ExecutionSummaryUpdatedEvent) => {
      this.pendingSummaries.push(event);
      this.scheduleFlush();
    });

    connection.on('executionNodesUpdated', (event: ExecutionNodesUpdatedEvent) => {
      this.pendingNodeUpdates.push(event);
      this.scheduleFlush();
    });

    connection.onreconnecting(() => {
      this.currentBlueprintId = null;
      this.currentWorkflowInstanceId = null;
    });

    connection.onreconnected(() => {
      this.currentBlueprintId = null;
      this.currentWorkflowInstanceId = null;
      void this.applyDesiredSubscriptions().finally(() => {
        this.reconnectedSubject.next();
      });
    });

    connection.onclose(() => {
      this.currentBlueprintId = null;
      this.currentWorkflowInstanceId = null;
      if (this.desiredConnected) {
        this.scheduleReconnect();
      }
    });

    return connection;
  }

  private scheduleReconnect(): void {
    if (!this.desiredConnected || this.reconnectTimeoutId !== null) {
      return;
    }

    this.reconnectTimeoutId = window.setTimeout(() => {
      this.reconnectTimeoutId = null;
      void this.ensureConnection();
    }, 5000);
  }

  private clearReconnectTimeout(): void {
    if (this.reconnectTimeoutId === null) {
      return;
    }

    window.clearTimeout(this.reconnectTimeoutId);
    this.reconnectTimeoutId = null;
  }

  private scheduleFlush(): void {
    if (this.flushHandle !== null) {
      return;
    }

    this.flushHandle = window.requestAnimationFrame(() => {
      this.flushHandle = null;
      this.flushPendingEvents();
    });
  }

  private flushPendingEvents(): void {
    if (this.pendingSummaries.length > 0) {
      const summariesByWorkflow = new Map<string, ExecutionSummaryUpdatedEvent>();
      for (const event of this.pendingSummaries.splice(0)) {
        summariesByWorkflow.set(event.workflowInstanceId, event);
      }

      this.summaryUpdatesSubject.next([...summariesByWorkflow.values()]);
    }

    if (this.pendingNodeUpdates.length > 0) {
      const nodeUpdatesByWorkflow = new Map<string, {
        workflowInstanceId: string;
        blueprintDefinitionId: number;
        blueprintVersionId: number;
        nodes: Map<string, ExecutionNodeDelta>;
      }>();

      for (const event of this.pendingNodeUpdates.splice(0)) {
        const current = nodeUpdatesByWorkflow.get(event.workflowInstanceId) ?? {
          workflowInstanceId: event.workflowInstanceId,
          blueprintDefinitionId: event.blueprintDefinitionId,
          blueprintVersionId: event.blueprintVersionId,
          nodes: new Map<string, ExecutionNodeDelta>(),
        };

        for (const node of event.nodes) {
          current.nodes.set(node.instanceId, node);
        }

        nodeUpdatesByWorkflow.set(event.workflowInstanceId, current);
      }

      this.nodeUpdatesSubject.next(
        [...nodeUpdatesByWorkflow.values()].map((event) => ({
          workflowInstanceId: event.workflowInstanceId,
          blueprintDefinitionId: event.blueprintDefinitionId,
          blueprintVersionId: event.blueprintVersionId,
          nodes: [...event.nodes.values()],
        })),
      );
    }
  }

  private clearPendingEvents(): void {
    this.pendingSummaries.length = 0;
    this.pendingNodeUpdates.length = 0;

    if (this.flushHandle !== null) {
      window.cancelAnimationFrame(this.flushHandle);
      this.flushHandle = null;
    }
  }
}
