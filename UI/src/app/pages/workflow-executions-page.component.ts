import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe, NgClass } from '@angular/common';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, distinctUntilChanged, map, of, startWith } from 'rxjs';
import {
  BlueprintEditorService,
  BlueprintViewModel,
  WorkflowExecutionDetailViewModel,
  WorkflowExecutionSummaryViewModel,
} from '../domain/blueprint-editor.service';
import {
  ExecutionNodesUpdatedEvent,
  ExecutionRealtimeService,
  ExecutionSummaryUpdatedEvent,
} from '../domain/execution-realtime.service';
import { NodeModel } from '../domain/node-model';
import { ConnectionModel } from '../domain/connection-model';
import { WorkflowCanvasComponent } from '../components/workflow-canvas.component';
import {
  ObservabilityService,
  WorkflowLogBatchViewModel,
  WorkflowLogEntryViewModel,
} from '../domain/observability.service';
import {
  createRequestStateStream,
  errorRequestState,
  idleRequestState,
  isErrorRequestState,
  isReadyRequestState,
  loadingRequestState,
  readyRequestState,
} from '../domain/request-state';
import { RuntimeClientConfigService } from '../domain/runtime-client-config.service';

interface WorkflowLogFilterOption {
  id: string;
  label: string;
}

interface ExecutionRouteSelection {
  blueprintId: number | null;
  workflowInstanceId: string | null;
}

interface WorkflowLogHistoryRequest {
  workflowInstanceId: string;
  from: string;
  to: string | null;
}

@Component({
  selector: 'app-workflow-executions-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatIconModule,
    NgClass,
    DatePipe,
    WorkflowCanvasComponent,
  ],
  templateUrl: './workflow-executions-page.component.html',
  styleUrl: './workflow-executions-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowExecutionsPageComponent {
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly executionRealtime = inject(ExecutionRealtimeService);
  private readonly observabilityService = inject(ObservabilityService);
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly canvasViewport = viewChild(WorkflowCanvasComponent);
  private readonly refreshVersion = signal(0);
  private readonly preferredWorkflowInstanceId = signal<string | null>(null);
  private readonly realtimeSummaryOverrides = signal(new Map<string, ExecutionSummaryUpdatedEvent>());
  private readonly realtimeNodeOverrides = signal(new Map<string, ExecutionNodesUpdatedEvent['nodes'][number]>());
  private readonly liveWorkflowLogs = signal<WorkflowLogEntryViewModel[]>([]);
  private workflowLogEventSource: EventSource | null = null;
  private readonly routeSelection = toSignal(
    this.route.queryParamMap.pipe(
      map((params): ExecutionRouteSelection => {
        const blueprintId = Number(params.get('blueprintId'));
        return {
          blueprintId: Number.isInteger(blueprintId) && blueprintId > 0 ? blueprintId : null,
          workflowInstanceId: params.get('workflowInstanceId'),
        };
      }),
      distinctUntilChanged((left, right) =>
        left.blueprintId === right.blueprintId &&
        left.workflowInstanceId === right.workflowInstanceId,
      ),
    ),
    {
      initialValue: {
        blueprintId: null,
        workflowInstanceId: null,
      } satisfies ExecutionRouteSelection,
    },
  );

  protected readonly pickerOpen = signal(false);
  protected readonly historyCollapsed = signal(false);
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly selectedExecutionId = signal<string | null>(null);
  protected readonly workflowLogPanelOpen = signal(false);
  protected readonly workflowLogStreamConnected = signal(false);
  protected readonly selectedWorkflowLogFilter = signal<string | null>(null);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly executionSearch = new FormControl('', { nonNullable: true });
  private readonly blueprintSearchQuery = toSignal(
    this.blueprintSearch.valueChanges.pipe(startWith(this.blueprintSearch.value)),
    { initialValue: this.blueprintSearch.value },
  );
  private readonly executionSearchQuery = toSignal(
    this.executionSearch.valueChanges.pipe(startWith(this.executionSearch.value)),
    { initialValue: this.executionSearch.value },
  );
  private readonly blueprintsState = toSignal(
    this.blueprintService.getBlueprints().pipe(
      map((blueprints) => readyRequestState<BlueprintViewModel[]>(blueprints)),
      startWith(loadingRequestState<BlueprintViewModel[]>()),
      catchError(() => of(errorRequestState<BlueprintViewModel[]>('Unable to load blueprints.'))),
    ),
    { initialValue: loadingRequestState<BlueprintViewModel[]>() },
  );
  protected readonly loading = computed(() => this.blueprintsState().status === 'loading');
  protected readonly blueprints = computed<BlueprintViewModel[]>(() => {
    const state = this.blueprintsState();
    return isReadyRequestState(state) ? state.data : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((candidate) => candidate.id === this.selectedBlueprintId()) ?? null,
  );
  private readonly executionSummariesState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const blueprint = this.selectedBlueprint();
        if (!blueprint) {
          return null;
        }

        return {
          blueprintId: blueprint.id,
          refreshVersion: this.refreshVersion(),
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintId === right?.blueprintId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
      ),
      ({ blueprintId }) => this.blueprintService.getBlueprintExecutions(blueprintId),
      'Unable to load executions for the selected blueprint.',
    ),
    { initialValue: idleRequestState<WorkflowExecutionSummaryViewModel[]>() },
  );
  protected readonly executionsLoading = computed(() => this.executionSummariesState().status === 'loading');
  private readonly fetchedExecutionSummaries = computed<WorkflowExecutionSummaryViewModel[]>(() => {
    const state = this.executionSummariesState();
    return isReadyRequestState(state) ? state.data : [];
  });
  protected readonly executionSummaries = computed<WorkflowExecutionSummaryViewModel[]>(() => {
    const selectedBlueprintId = this.selectedBlueprint()?.id;
    const summariesByWorkflowId = new Map<string, WorkflowExecutionSummaryViewModel>(
      this.fetchedExecutionSummaries().map((summary) => [summary.workflowInstanceId, summary] as const),
    );

    if (selectedBlueprintId !== undefined) {
      for (const update of this.realtimeSummaryOverrides().values()) {
        if (update.blueprintDefinitionId !== selectedBlueprintId) {
          continue;
        }

        summariesByWorkflowId.set(update.workflowInstanceId, {
          workflowInstanceId: update.workflowInstanceId,
          blueprintDefinitionId: update.blueprintDefinitionId,
          blueprintVersionId: update.blueprintVersionId,
          blueprintVersionNumber: update.blueprintVersionNumber,
          blueprintVersionName: update.blueprintVersionName,
          status: update.status,
          createdAt: update.createdAt,
          updatedAt: update.updatedAt ?? null,
          completedTaskCount: update.completedTaskCount,
          failedTaskCount: update.failedTaskCount,
          inProgressTaskCount: update.inProgressTaskCount,
          pendingTaskCount: update.pendingTaskCount,
        });
      }
    }

    return [...summariesByWorkflowId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  });
  protected readonly activeExecutionId = computed(() => {
    const currentExecutionId = this.selectedExecutionId();
    const executions = this.executionSummaries();
    if (currentExecutionId && executions.some((execution) => execution.workflowInstanceId === currentExecutionId)) {
      return currentExecutionId;
    }

    const preferredExecutionId = this.preferredWorkflowInstanceId();
    if (preferredExecutionId && executions.some((execution) => execution.workflowInstanceId === preferredExecutionId)) {
      return preferredExecutionId;
    }

    return executions[0]?.workflowInstanceId ?? null;
  });
  private readonly selectedExecutionState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const blueprint = this.selectedBlueprint();
        const workflowInstanceId = this.activeExecutionId();
        if (!blueprint || !workflowInstanceId) {
          return null;
        }

        return {
          blueprintId: blueprint.id,
          workflowInstanceId,
          refreshVersion: this.refreshVersion(),
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintId === right?.blueprintId &&
          left?.workflowInstanceId === right?.workflowInstanceId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
      ),
      ({ blueprintId, workflowInstanceId }) =>
        this.blueprintService.getBlueprintExecution(blueprintId, workflowInstanceId),
      'Unable to load the selected execution.',
    ),
    { initialValue: idleRequestState<WorkflowExecutionDetailViewModel>() },
  );
  protected readonly detailLoading = computed(() => this.selectedExecutionState().status === 'loading');
  private readonly fetchedExecution = computed<WorkflowExecutionDetailViewModel | null>(() => {
    const state = this.selectedExecutionState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly selectedExecution = computed<WorkflowExecutionDetailViewModel | null>(() => {
    const execution = this.fetchedExecution();
    if (!execution) {
      return null;
    }

    const summaryOverride = this.realtimeSummaryOverrides().get(execution.workflowInstanceId);
    const nodeOverrides = this.realtimeNodeOverrides();

    return {
      ...execution,
      blueprintVersionId: summaryOverride?.blueprintVersionId ?? execution.blueprintVersionId,
      blueprintVersionNumber: summaryOverride?.blueprintVersionNumber ?? execution.blueprintVersionNumber,
      blueprintVersionName: summaryOverride?.blueprintVersionName ?? execution.blueprintVersionName,
      status: summaryOverride?.status ?? execution.status,
      updatedAt: summaryOverride?.updatedAt ?? execution.updatedAt,
      nodes: execution.nodes.map((node) => {
        const delta = nodeOverrides.get(node.instanceId);
        if (!delta) {
          return node;
        }

        return {
          ...node,
          runtimeTaskInstanceId: delta.taskInstanceId,
          status: delta.status,
          resolvedVersionId: delta.resolvedVersionId,
          resolvedVersion: delta.resolvedVersion ?? node.resolvedVersion,
          startedAt: delta.startedAt ?? node.startedAt,
          completedAt: delta.completedAt ?? node.completedAt,
          failedAt: delta.failedAt ?? node.failedAt,
          failureMessage: delta.failureMessage ?? null,
        };
      }),
    };
  });
  private readonly workflowLogHistoryRequest = computed<WorkflowLogHistoryRequest | null>(() => {
    const execution = this.selectedExecution();
    if (!this.workflowLogPanelOpen() || !execution) {
      return null;
    }

    return {
      workflowInstanceId: execution.workflowInstanceId,
      from: execution.createdAt,
      to: execution.status === 'InProgress' ? null : execution.updatedAt ?? null,
    };
  });
  private readonly workflowLogHistoryState = toSignal(
    createRequestStateStream(
      toObservable(this.workflowLogHistoryRequest).pipe(
        distinctUntilChanged((left, right) =>
          left?.workflowInstanceId === right?.workflowInstanceId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
      ),
      (request) => this.observabilityService.getWorkflowLogs(request.workflowInstanceId, {
        from: request.from,
        to: request.to,
        limit: 400,
      }),
      'Unable to load logs for that execution.',
    ),
    { initialValue: idleRequestState<WorkflowLogBatchViewModel>() },
  );
  protected readonly workflowLogsLoading = computed(() => this.workflowLogHistoryState().status === 'loading');
  protected readonly workflowLogsError = computed(() => {
    const state = this.workflowLogHistoryState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly workflowLogs = computed<WorkflowLogEntryViewModel[]>(() => {
    const historyState = this.workflowLogHistoryState();
    const historyEntries = isReadyRequestState(historyState)
      ? historyState.data.entries
      : [];
    const entriesByCursor = new Map<string, WorkflowLogEntryViewModel>();

    for (const entry of historyEntries) {
      entriesByCursor.set(entry.cursor, entry);
    }

    for (const entry of this.liveWorkflowLogs()) {
      entriesByCursor.set(entry.cursor, entry);
    }

    return [...entriesByCursor.values()]
      .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp));
  });
  private readonly workflowLogStreamRequest = computed(() => {
    const execution = this.selectedExecution();
    const historyState = this.workflowLogHistoryState();
    if (!this.workflowLogPanelOpen() || !execution || execution.status !== 'InProgress' || historyState.status !== 'ready') {
      return null;
    }

    return {
      workflowInstanceId: execution.workflowInstanceId,
      from: execution.createdAt,
      cursor: historyState.data.nextCursor ?? historyState.data.entries.at(-1)?.cursor ?? null,
    };
  });
  protected readonly zoomPercent = computed(() => this.canvasViewport()?.zoomPercent() ?? 100);
  protected readonly selectedExecutionGrafanaLogsUrl = computed(() => {
    const execution = this.selectedExecution();
    if (!execution) {
      return this.runtimeClientConfig.grafanaBaseUrl;
    }

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'loki',
      queries: [
        {
          refId: 'A',
          expr: `{workflow_instance_id="${execution.workflowInstanceId}"}`,
        },
      ],
      range: {
        from: execution.createdAt,
        to: execution.updatedAt,
      },
    }));

    return `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1&left=${left}`;
  });
  protected readonly selectedExecutionGrafanaTraceUrl = computed(() => {
    const execution = this.selectedExecution();
    if (!execution) {
      return this.runtimeClientConfig.grafanaBaseUrl;
    }

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'tempo',
      queries: [
        {
          refId: 'A',
          query: execution.workflowInstanceId,
        },
      ],
      range: {
        from: 'now-1h',
        to: 'now',
      },
    }));

    return `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1&left=${left}`;
  });
  protected readonly workflowLogFilterOptions = computed<WorkflowLogFilterOption[]>(() => {
    const detail = this.selectedExecution();
    if (!detail) {
      return [];
    }

    const options = detail.nodes.map((node) => ({
      id: node.instanceId,
      label: node.name,
    }));

    if (this.workflowLogs().some((entry) => !entry.blueprintTaskId)) {
      options.unshift({
        id: '__workflow__',
        label: 'Workflow / System',
      });
    }

    return options;
  });
  protected readonly filteredWorkflowLogs = computed(() => {
    const filterId = this.selectedWorkflowLogFilter();
    if (!filterId) {
      return this.workflowLogs();
    }

    if (filterId === '__workflow__') {
      return this.workflowLogs().filter((entry) => !entry.blueprintTaskId);
    }

    return this.workflowLogs().filter((entry) => entry.blueprintTaskId === filterId);
  });

  protected readonly filteredBlueprints = computed(() => {
    const query = this.blueprintSearchQuery().trim().toLocaleLowerCase();
    const blueprints = [...this.blueprints()].sort((left, right) => left.name.localeCompare(right.name));
    if (!query) {
      return blueprints;
    }

    return blueprints.filter((blueprint) =>
      blueprint.name.toLocaleLowerCase().includes(query),
    );
  });

  protected readonly filteredExecutions = computed(() => {
    const query = this.executionSearchQuery().trim().toLocaleLowerCase();
    if (!query) {
      return this.executionSummaries();
    }

    return this.executionSummaries().filter((execution) => {
      const searchHaystack = [
        execution.blueprintVersionName,
        execution.blueprintVersionNumber.toString(),
        execution.workflowInstanceId,
        execution.status,
        execution.createdAt,
      ].join(' ').toLocaleLowerCase();

      return searchHaystack.includes(query);
    });
  });

  protected readonly executionCountLabel = computed(() => {
    const count = this.executionSummaries().length;
    return count === 1 ? '1 run' : `${count} runs`;
  });
  protected readonly errorMessage = computed(() => {
    const blueprintsState = this.blueprintsState();
    if (isErrorRequestState(blueprintsState)) {
      return blueprintsState.message;
    }

    const executionSummariesState = this.executionSummariesState();
    if (isErrorRequestState(executionSummariesState)) {
      return executionSummariesState.message;
    }

    const selectedExecutionState = this.selectedExecutionState();
    if (isErrorRequestState(selectedExecutionState)) {
      return selectedExecutionState.message;
    }

    return null;
  });

  protected readonly executionNodes = computed(() => {
    const detail = this.selectedExecution();
    if (!detail) {
      return [];
    }

    return detail.nodes.map((node, index) => new NodeModel(
      index + 1,
      node.taskDefinitionId,
      node.allowedMajor,
      node.allowedMinor,
      node.resolvedVersionId,
      node.resolvedVersion ?? null,
      node.name,
      node.position.x,
      node.position.y,
      320,
      Math.max(220, 168 + Math.max(node.outputChannelNames.length, 1) * 34),
      node.inputChannelName ?? null,
      node.outputChannelNames,
      false,
      node.status,
      node.failureMessage ?? null,
    ));
  });

  protected readonly executionConnections = computed(() => {
    const detail = this.selectedExecution();
    const nodes = this.executionNodes();
    if (!detail || nodes.length === 0) {
      return [];
    }

    const nodeByInstanceId = new Map(detail.nodes.map((node, index) => [node.instanceId, nodes[index]] as const));
    return detail.nodes.flatMap((node) => {
      const fromNode = nodeByInstanceId.get(node.instanceId);
      if (!fromNode) {
        return [];
      }

      return node.outgoingConnections.flatMap((connection) => {
        const toNode = nodeByInstanceId.get(connection.taskInstanceId);
        if (!toNode) {
          return [];
        }

        return [new ConnectionModel(
          fromNode,
          toNode,
          connection.anchor,
          connection.side,
          connection.position,
        )];
      });
    });
  });

  constructor() {
    effect(() => {
      this.executionRealtime.setSubscriptions(
        this.selectedBlueprintId() ?? this.routeSelection().blueprintId,
        this.selectedExecutionId() ??
        this.preferredWorkflowInstanceId() ??
        this.routeSelection().workflowInstanceId ??
        this.activeExecutionId(),
      );
    });

    effect(() => {
      const blueprint = this.selectedBlueprint();
      const nextValue = blueprint?.name ?? '';
      if (this.blueprintSearch.value !== nextValue) {
        this.blueprintSearch.setValue(nextValue, { emitEvent: false });
      }
    });

    effect(() => {
      const execution = this.fetchedExecution();
      if (!execution) {
        return;
      }

      this.centerOnStartingNode(execution);
    });

    effect((onCleanup) => {
      const request = this.workflowLogStreamRequest();
      this.stopWorkflowLogStream();

      if (!request) {
        return;
      }

      const eventSource = this.observabilityService.createWorkflowLogStream(
        request.workflowInstanceId,
        {
          from: request.from,
          cursor: request.cursor,
        },
      );

      eventSource.addEventListener('logBatch', (event) => {
        const messageEvent = event as MessageEvent<string>;
        const batch = JSON.parse(messageEvent.data) as WorkflowLogBatchViewModel;
        this.appendWorkflowLogEntries(batch.entries);
        this.workflowLogStreamConnected.set(true);
      });

      eventSource.onerror = () => {
        this.workflowLogStreamConnected.set(false);
      };

      this.workflowLogEventSource = eventSource;

      onCleanup(() => {
        eventSource.close();
        if (this.workflowLogEventSource === eventSource) {
          this.workflowLogEventSource = null;
        }
        this.workflowLogStreamConnected.set(false);
      });
    });

    this.blueprintSearch.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.pickerOpen.set(true);
      });

    toObservable(this.routeSelection)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((selection) => {
        this.applyBlueprintSelection(
          selection.blueprintId,
          selection.workflowInstanceId,
        );
      });

    this.executionRealtime.summaryUpdates$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((updates) => this.applyRealtimeSummaryUpdates(updates));

    this.executionRealtime.nodeUpdates$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((updates) => this.applyRealtimeNodeUpdates(updates));

    this.executionRealtime.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.refreshVersion.update((current) => current + 1);
      });

    toObservable(this.workflowLogHistoryRequest)
      .pipe(
        distinctUntilChanged((left, right) =>
          left?.workflowInstanceId === right?.workflowInstanceId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((request) => {
        this.liveWorkflowLogs.set([]);
        this.selectedWorkflowLogFilter.set(null);
        if (!request) {
          this.stopWorkflowLogStream();
        }
      });

    this.executionRealtime.connect();
    this.destroyRef.onDestroy(() => {
      this.stopWorkflowLogStream();
      void this.executionRealtime.disconnect();
    });
  }

  protected toggleHistoryPanel(): void {
    this.historyCollapsed.update((collapsed) => !collapsed);
  }

  protected selectBlueprint(blueprint: BlueprintViewModel): void {
    this.applyBlueprintSelection(blueprint.id, null);
  }

  protected openPicker(): void {
    this.pickerOpen.set(true);
  }

  protected closePicker(event: FocusEvent): void {
    const currentTarget = event.currentTarget as HTMLElement | null;
    const nextTarget = event.relatedTarget as Node | null;
    if (currentTarget && nextTarget && currentTarget.contains(nextTarget)) {
      return;
    }

    window.setTimeout(() => this.pickerOpen.set(false), 120);
  }

  protected selectExecution(execution: WorkflowExecutionSummaryViewModel): void {
    this.selectedExecutionId.set(execution.workflowInstanceId);
    this.preferredWorkflowInstanceId.set(null);
  }

  protected resetView(): void {
    this.canvasViewport()?.resetView();
  }

  protected zoomIn(): void {
    this.canvasViewport()?.zoomIn();
  }

  protected zoomOut(): void {
    this.canvasViewport()?.zoomOut();
  }

  protected toggleWorkflowLogs(): void {
    if (!this.selectedExecution()) {
      return;
    }

    if (this.workflowLogPanelOpen()) {
      this.closeWorkflowLogs();
      return;
    }

    this.workflowLogPanelOpen.set(true);
  }

  protected closeWorkflowLogPanel(): void {
    this.closeWorkflowLogs();
  }

  protected selectWorkflowLogFilter(filterId: string | null): void {
    this.selectedWorkflowLogFilter.set(filterId);
  }

  protected statusLabel(status: string): string {
    switch (status) {
      case 'Completed':
        return 'Succeeded';
      case 'InProgress':
        return 'Running';
      case 'Failed':
        return 'Execution Failed';
      case 'Pending':
        return 'Pending';
      default:
        return 'Not Executed';
    }
  }

  protected statusIcon(status: string): string {
    switch (status) {
      case 'Completed':
        return 'check_circle';
      case 'InProgress':
        return 'hourglass_top';
      case 'Failed':
        return 'error';
      case 'Pending':
        return 'schedule';
      default:
        return 'radio_button_unchecked';
    }
  }

  protected workflowLogLabel(entry: WorkflowLogEntryViewModel): string {
    return entry.taskName ?? entry.pluginName ?? 'Workflow / System';
  }

  protected workflowLogFilterLabel(filter: WorkflowLogFilterOption): string {
    return filter.label;
  }

  protected trackBlueprint(_: number, blueprint: BlueprintViewModel): number {
    return blueprint.id;
  }

  protected trackExecution(_: number, execution: WorkflowExecutionSummaryViewModel): string {
    return execution.workflowInstanceId;
  }

  protected trackWorkflowLog(_: number, entry: WorkflowLogEntryViewModel): string {
    return entry.cursor;
  }

  private applyBlueprintSelection(
    blueprintId: number | null,
    preferredWorkflowInstanceId: string | null,
  ): void {
    this.selectedBlueprintId.set(blueprintId);
    this.preferredWorkflowInstanceId.set(preferredWorkflowInstanceId);
    this.executionSearch.setValue('', { emitEvent: false });
    this.pickerOpen.set(false);
    this.selectedExecutionId.set(null);
    this.historyCollapsed.set(false);
    this.closeWorkflowLogs();
    this.realtimeSummaryOverrides.set(new Map());
    this.realtimeNodeOverrides.set(new Map());
  }

  private applyRealtimeSummaryUpdates(updates: ExecutionSummaryUpdatedEvent[]): void {
    const selectedBlueprintId = this.selectedBlueprint()?.id;
    if (!selectedBlueprintId) {
      return;
    }

    const relevantUpdates = updates.filter((update) => update.blueprintDefinitionId === selectedBlueprintId);
    if (relevantUpdates.length === 0) {
      return;
    }

    this.realtimeSummaryOverrides.update((currentOverrides) => {
      const nextOverrides = new Map(currentOverrides);
      for (const update of relevantUpdates) {
        nextOverrides.set(update.workflowInstanceId, update);
      }
      return nextOverrides;
    });
  }

  private applyRealtimeNodeUpdates(updates: ExecutionNodesUpdatedEvent[]): void {
    const activeExecutionId = this.activeExecutionId();
    if (!activeExecutionId) {
      return;
    }

    const relevantUpdates = updates.filter((update) => update.workflowInstanceId === activeExecutionId);
    if (relevantUpdates.length === 0) {
      return;
    }

    this.realtimeNodeOverrides.update((currentOverrides) => {
      const nextOverrides = new Map(currentOverrides);
      for (const update of relevantUpdates) {
        for (const node of update.nodes) {
          nextOverrides.set(node.instanceId, node);
        }
      }
      return nextOverrides;
    });
  }

  private centerOnStartingNode(detail: WorkflowExecutionDetailViewModel): void {
    const nodeIndex = detail.nodes.findIndex((node) => node.instanceId === detail.startingTaskInstanceId);
    if (nodeIndex < 0) {
      return;
    }

    queueMicrotask(() => this.canvasViewport()?.centerOnNode(nodeIndex + 1));
  }

  private appendWorkflowLogEntries(entries: WorkflowLogEntryViewModel[]): void {
    if (entries.length === 0) {
      return;
    }

    this.liveWorkflowLogs.update((currentEntries) => {
      const entriesByCursor = new Map(currentEntries.map((entry) => [entry.cursor, entry] as const));
      for (const entry of entries) {
        entriesByCursor.set(entry.cursor, entry);
      }

      return [...entriesByCursor.values()]
        .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp));
    });
  }

  private closeWorkflowLogs(): void {
    this.workflowLogPanelOpen.set(false);
    this.liveWorkflowLogs.set([]);
    this.selectedWorkflowLogFilter.set(null);
    this.stopWorkflowLogStream();
  }

  private stopWorkflowLogStream(): void {
    this.workflowLogEventSource?.close();
    this.workflowLogEventSource = null;
    this.workflowLogStreamConnected.set(false);
  }
}
