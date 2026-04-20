import { Injectable, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { RuntimeClientConfigService } from './runtime-client-config.service';

export interface ProcessSummaryUpdatedEvent {
  processId: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
  completedTaskCount: number;
  failedTaskCount: number;
  inProgressTaskCount: number;
  pendingTaskCount: number;
}

export interface ProcessStepDelta {
  instanceId: string;
  blueprintNodeId: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  resolvedVersionId: number;
  resolvedVersion?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  failedAt?: string | null;
  failureMessage?: string | null;
  updatedAt?: string | null;
}

export interface ProcessStepsUpdatedEvent {
  processId: string;
  blueprintDefinitionId: number;
  blueprintVersionId: number;
  nodes: ProcessStepDelta[];
}

@Injectable({ providedIn: 'root' })
export class ProcessRealtimeService {
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);
  private connection: HubConnection | null = null;
  private startPromise: Promise<void> | null = null;
  private reconnectTimeoutId: number | null = null;
  private flushScheduled = false;

  private desiredConnected = false;
  private desiredBlueprintId: number | null = null;
  private desiredProcessId: string | null = null;
  private currentBlueprintId: number | null = null;
  private currentProcessId: string | null = null;

  private readonly pendingSummaries: ProcessSummaryUpdatedEvent[] = [];
  private readonly pendingNodeUpdates: ProcessStepsUpdatedEvent[] = [];

  private readonly summaryUpdatesSubject = new Subject<ProcessSummaryUpdatedEvent[]>();
  private readonly nodeUpdatesSubject = new Subject<ProcessStepsUpdatedEvent[]>();
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
    this.desiredProcessId = null;
    this.currentBlueprintId = null;
    this.currentProcessId = null;
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
      console.warn('Failed to stop process SignalR connection.', error);
    }
  }

  setSubscriptions(blueprintDefinitionId: number | null, processId: string | null): void {
    this.desiredBlueprintId = blueprintDefinitionId;
    this.desiredProcessId = processId;
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
        console.warn('Failed to start process SignalR connection.', error);
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

    if (this.currentProcessId !== null &&
      this.currentProcessId !== this.desiredProcessId) {
      await this.connection.invoke('UnsubscribeProcess', this.currentProcessId);
      this.currentProcessId = null;
    }

    if (this.desiredBlueprintId !== null && this.currentBlueprintId !== this.desiredBlueprintId) {
      await this.connection.invoke('SubscribeBlueprint', this.desiredBlueprintId);
      this.currentBlueprintId = this.desiredBlueprintId;
    }

    if (this.desiredProcessId !== null &&
      this.currentProcessId !== this.desiredProcessId) {
      await this.connection.invoke('SubscribeProcess', this.desiredProcessId);
      this.currentProcessId = this.desiredProcessId;
    }
  }

  private createConnection(): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl(`${this.runtimeClientConfig.signalRBaseUrl}/hubs/processes`, {
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('processSummaryUpdated', (event: ProcessSummaryUpdatedEvent) => {
      this.pendingSummaries.push(event);
      this.scheduleFlush();
    });

    connection.on('processStepsUpdated', (event: ProcessStepsUpdatedEvent) => {
      this.pendingNodeUpdates.push(event);
      this.scheduleFlush();
    });

    connection.onreconnecting(() => {
      this.currentBlueprintId = null;
      this.currentProcessId = null;
    });

    connection.onreconnected(() => {
      this.currentBlueprintId = null;
      this.currentProcessId = null;
      void this.applyDesiredSubscriptions().finally(() => {
        this.reconnectedSubject.next();
      });
    });

    connection.onclose(() => {
      this.currentBlueprintId = null;
      this.currentProcessId = null;
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
    if (this.flushScheduled) {
      return;
    }

    this.flushScheduled = true;
    queueMicrotask(() => {
      this.flushScheduled = false;
      this.flushPendingEvents();
    });
  }

  private flushPendingEvents(): void {
    if (this.pendingSummaries.length > 0) {
      const summariesByProcess = new Map<string, ProcessSummaryUpdatedEvent>();
      for (const event of this.pendingSummaries.splice(0)) {
        summariesByProcess.set(event.processId, event);
      }

      this.summaryUpdatesSubject.next([...summariesByProcess.values()]);
    }

    if (this.pendingNodeUpdates.length > 0) {
      const nodeUpdatesByProcess = new Map<string, {
        processId: string;
        blueprintDefinitionId: number;
        blueprintVersionId: number;
        nodes: Map<string, ProcessStepDelta>;
      }>();

      for (const event of this.pendingNodeUpdates.splice(0)) {
        const current = nodeUpdatesByProcess.get(event.processId) ?? {
          processId: event.processId,
          blueprintDefinitionId: event.blueprintDefinitionId,
          blueprintVersionId: event.blueprintVersionId,
          nodes: new Map<string, ProcessStepDelta>(),
        };

        for (const node of event.nodes) {
          current.nodes.set(node.instanceId, node);
        }

        nodeUpdatesByProcess.set(event.processId, current);
      }

      this.nodeUpdatesSubject.next(
        [...nodeUpdatesByProcess.values()].map((event) => ({
          processId: event.processId,
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
    this.flushScheduled = false;
  }
}
