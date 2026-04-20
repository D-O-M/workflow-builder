import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
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
import { MatMenuModule } from '@angular/material/menu';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  firstValueFrom,
  map,
  of,
  startWith,
  switchMap,
  tap,
} from 'rxjs';
import {
  BlueprintEditorService,
  BlueprintExecutionActivityViewModel,
  BlueprintExecutionHistoryEntryViewModel,
  BlueprintViewModel,
  WorkflowExecutionDetailViewModel,
  WorkflowExecutionStepDetailViewModel,
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
  NodeLogBatchViewModel,
  NodeLogEntryViewModel,
  ObservabilityService,
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

interface ExecutionRouteSelection {
  blueprintId: number | null;
  workflowInstanceId: string | null;
}

interface StepLogHistoryRequest {
  blueprintDefinitionId: number;
  workflowInstanceId: string;
  blueprintTaskInstanceId: string;
  from: string;
  to: string | null;
}

type HistoryPanelMode = 'blueprints' | 'executions';
type StepInspectorTab = 'inputs' | 'outputs' | 'logs';

@Component({
  selector: 'app-workflow-executions-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
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
  private readonly executionInfoButton = viewChild<ElementRef<HTMLElement>>('executionInfoButton');
  private readonly executionInfoPopover = viewChild<ElementRef<HTMLElement>>('executionInfoPopover');
  private readonly refreshVersion = signal(0);
  private readonly preferredWorkflowInstanceId = signal<string | null>(null);
  private readonly realtimeSummaryOverrides = signal(new Map<string, ExecutionSummaryUpdatedEvent>());
  private readonly realtimeNodeOverrides = signal(new Map<string, ExecutionNodesUpdatedEvent['nodes'][number]>());
  private readonly liveStepLogs = signal<NodeLogEntryViewModel[]>([]);
  private stepLogEventSource: EventSource | null = null;
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

  protected readonly historyCollapsed = signal(false);
  protected readonly historyPanelMode = signal<HistoryPanelMode>('blueprints');
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly selectedExecutionId = signal<string | null>(null);
  protected readonly selectedStepInstanceId = signal<string | null>(null);
  protected readonly stepInspectorTab = signal<StepInspectorTab>('inputs');
  protected readonly stepLogStreamConnected = signal(false);
  protected readonly executionInfoOpen = signal(false);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });

  private readonly blueprintSearchQuery = toSignal(
    this.blueprintSearch.valueChanges.pipe(
      startWith(this.blueprintSearch.value),
      map((value) => value.trim()),
      debounceTime(200),
      distinctUntilChanged(),
    ),
    { initialValue: this.blueprintSearch.value.trim() },
  );

  private readonly blueprintsState = toSignal(
    this.blueprintService.getBlueprints().pipe(
      map((blueprints) => readyRequestState<BlueprintViewModel[]>(blueprints)),
      startWith(loadingRequestState<BlueprintViewModel[]>()),
      catchError(() => of(errorRequestState<BlueprintViewModel[]>('Unable to load blueprints.'))),
    ),
    { initialValue: loadingRequestState<BlueprintViewModel[]>() },
  );

  private readonly blueprintActivityRows = signal<BlueprintExecutionActivityViewModel[]>([]);
  private readonly blueprintActivityPage = signal(1);
  private readonly blueprintActivityTotalCount = signal(0);
  protected readonly blueprintActivityHasMore = signal(false);
  protected readonly blueprintActivityLoading = signal(false);
  protected readonly blueprintActivityLoadingMore = signal(false);
  protected readonly blueprintActivityError = signal<string | null>(null);

  private readonly executionHistoryBaseRows = signal<BlueprintExecutionHistoryEntryViewModel[]>([]);
  private readonly executionHistoryNextCursor = signal<string | null>(null);
  private readonly executionHistoryHasMoreState = signal(false);
  protected readonly executionHistoryLoading = signal(false);
  protected readonly executionHistoryLoadingMore = signal(false);
  protected readonly executionHistoryError = signal<string | null>(null);

  protected readonly loading = computed(() => this.blueprintsState().status === 'loading');
  protected readonly blueprints = computed<BlueprintViewModel[]>(() => {
    const state = this.blueprintsState();
    return isReadyRequestState(state) ? state.data : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((candidate) => candidate.id === this.selectedBlueprintId()) ?? null,
  );
  protected readonly blueprintActivities = computed(() => this.blueprintActivityRows());
  protected readonly executionHistoryRows = computed<BlueprintExecutionHistoryEntryViewModel[]>(() => {
    const selectedBlueprintId = this.selectedBlueprintId();
    const rowsByWorkflowId = new Map<string, BlueprintExecutionHistoryEntryViewModel>(
      this.executionHistoryBaseRows().map((row) => [row.workflowInstanceId, row] as const),
    );

    if (selectedBlueprintId !== null) {
      for (const update of this.realtimeSummaryOverrides().values()) {
        if (update.blueprintDefinitionId !== selectedBlueprintId) {
          continue;
        }

        const existingRow = rowsByWorkflowId.get(update.workflowInstanceId);
        rowsByWorkflowId.set(update.workflowInstanceId, {
          workflowInstanceId: update.workflowInstanceId,
          blueprintDefinitionId: update.blueprintDefinitionId,
          blueprintVersionId: update.blueprintVersionId,
          blueprintVersionNumber: update.blueprintVersionNumber,
          blueprintVersionName: update.blueprintVersionName,
          status: update.status,
          createdAt: existingRow?.createdAt ?? update.createdAt,
          updatedAt: update.updatedAt ?? existingRow?.updatedAt ?? null,
        });
      }
    }

    return [...rowsByWorkflowId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  });
  protected readonly executionHistoryHasMore = computed(() => this.executionHistoryHasMoreState());
  protected readonly activeExecutionId = computed(() => {
    const currentExecutionId = this.selectedExecutionId();
    if (currentExecutionId) {
      return currentExecutionId;
    }

    const preferredExecutionId = this.preferredWorkflowInstanceId();
    if (preferredExecutionId) {
      return preferredExecutionId;
    }

    return this.executionHistoryRows()[0]?.workflowInstanceId ?? null;
  });
  private readonly selectedExecutionState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const blueprintId = this.selectedBlueprintId();
        const workflowInstanceId = this.activeExecutionId();
        if (!blueprintId || !workflowInstanceId) {
          return null;
        }

        return {
          blueprintId,
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
  private readonly executionNodeViewByCanvasId = computed(() =>
    new Map(
      (this.selectedExecution()?.nodes ?? []).map((node, index) => [index + 1, node] as const),
    ),
  );
  private readonly executionCanvasNodeIdByInstanceId = computed(() =>
    new Map(
      (this.selectedExecution()?.nodes ?? []).map((node, index) => [node.instanceId, index + 1] as const),
    ),
  );
  protected readonly selectedExecutionCanvasNodeId = computed(() => {
    const selectedStepInstanceId = this.selectedStepInstanceId();
    if (!selectedStepInstanceId) {
      return null;
    }

    return this.executionCanvasNodeIdByInstanceId().get(selectedStepInstanceId) ?? null;
  });
  protected readonly selectedExecutionStepNode = computed(() => {
    const selectedStepInstanceId = this.selectedStepInstanceId();
    if (!selectedStepInstanceId) {
      return null;
    }

    return this.selectedExecution()?.nodes.find((node) => node.instanceId === selectedStepInstanceId) ?? null;
  });
  protected readonly stepDrawerOpen = computed(() => this.selectedStepInstanceId() !== null);
  private readonly selectedStepState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const execution = this.selectedExecution();
        const blueprintTaskInstanceId = this.selectedStepInstanceId();
        if (!execution || !blueprintTaskInstanceId) {
          return null;
        }

        return {
          blueprintDefinitionId: execution.blueprintDefinitionId,
          workflowInstanceId: execution.workflowInstanceId,
          blueprintTaskInstanceId,
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.workflowInstanceId === right?.workflowInstanceId &&
          left?.blueprintTaskInstanceId === right?.blueprintTaskInstanceId,
        ),
      ),
      ({ blueprintDefinitionId, workflowInstanceId, blueprintTaskInstanceId }) =>
        this.blueprintService.getExecutionStepDetail(
          blueprintDefinitionId,
          workflowInstanceId,
          blueprintTaskInstanceId,
        ),
      'Unable to load the selected step.',
    ),
    { initialValue: idleRequestState<WorkflowExecutionStepDetailViewModel>() },
  );
  protected readonly stepDetailLoading = computed(() => this.selectedStepState().status === 'loading');
  protected readonly selectedStepDetail = computed<WorkflowExecutionStepDetailViewModel | null>(() => {
    const state = this.selectedStepState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly stepDetailError = computed(() => {
    const state = this.selectedStepState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly selectedStepTitle = computed(() =>
    this.selectedStepDetail()?.name
    ?? this.selectedExecutionStepNode()?.name
    ?? 'Step details',
  );
  protected readonly selectedStepStatus = computed(() =>
    this.selectedStepDetail()?.status
    ?? this.selectedExecutionStepNode()?.status
    ?? 'NotExecuted',
  );
  protected readonly selectedStepResolvedVersion = computed(() =>
    this.selectedStepDetail()?.resolvedVersion
    ?? this.selectedExecutionStepNode()?.resolvedVersion
    ?? null,
  );
  protected readonly selectedStepStartedAt = computed(() =>
    this.selectedStepDetail()?.startedAt
    ?? this.selectedExecutionStepNode()?.startedAt
    ?? null,
  );
  protected readonly selectedStepEndedAt = computed(() =>
    this.selectedStepDetail()?.endedAt
    ?? this.selectedExecutionStepNode()?.failedAt
    ?? this.selectedExecutionStepNode()?.completedAt
    ?? null,
  );
  protected readonly selectedStepDurationMs = computed(() =>
    this.selectedStepDetail()?.durationMs ?? null,
  );
  protected readonly selectedStepFailureMessage = computed(() =>
    this.selectedStepDetail()?.failureMessage
    ?? this.selectedExecutionStepNode()?.failureMessage
    ?? null,
  );
  protected readonly selectedStepInputs = computed(() => this.selectedStepDetail()?.inputs ?? null);
  protected readonly selectedStepOutputs = computed(() => this.selectedStepDetail()?.outputs ?? null);
  private readonly stepLogHistoryRequest = computed<StepLogHistoryRequest | null>(() => {
    const execution = this.selectedExecution();
    const blueprintTaskInstanceId = this.selectedStepInstanceId();
    if (!execution || !blueprintTaskInstanceId || this.stepInspectorTab() !== 'logs') {
      return null;
    }

    const startedAt = this.selectedStepStartedAt() ?? execution.createdAt;
    const status = this.selectedStepStatus();

    return {
      blueprintDefinitionId: execution.blueprintDefinitionId,
      workflowInstanceId: execution.workflowInstanceId,
      blueprintTaskInstanceId,
      from: startedAt,
      to: status === 'InProgress' ? null : this.selectedStepEndedAt() ?? execution.updatedAt ?? null,
    };
  });
  private readonly stepLogHistoryState = toSignal(
    createRequestStateStream(
      toObservable(this.stepLogHistoryRequest).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.workflowInstanceId === right?.workflowInstanceId &&
          left?.blueprintTaskInstanceId === right?.blueprintTaskInstanceId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
      ),
      (request) => this.observabilityService.getExecutionStepLogs(
        request.blueprintDefinitionId,
        request.workflowInstanceId,
        request.blueprintTaskInstanceId,
        {
          from: request.from,
          to: request.to,
          limit: 400,
        },
      ),
      'Unable to load logs for that step.',
    ),
    { initialValue: idleRequestState<NodeLogBatchViewModel>() },
  );
  protected readonly stepLogsLoading = computed(() => this.stepLogHistoryState().status === 'loading');
  protected readonly stepLogsError = computed(() => {
    const state = this.stepLogHistoryState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly stepLogs = computed<NodeLogEntryViewModel[]>(() => {
    const historyState = this.stepLogHistoryState();
    const historyEntries = isReadyRequestState(historyState)
      ? historyState.data.entries
      : [];
    const entriesByCursor = new Map<string, NodeLogEntryViewModel>();

    for (const entry of historyEntries) {
      entriesByCursor.set(entry.cursor, entry);
    }

    for (const entry of this.liveStepLogs()) {
      entriesByCursor.set(entry.cursor, entry);
    }

      return [...entriesByCursor.values()]
      .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp));
  });
  protected readonly stepLogCount = computed(() => this.stepLogs().length);
  private readonly stepLogStreamRequest = computed(() => {
    const request = this.stepLogHistoryRequest();
    const historyState = this.stepLogHistoryState();
    if (!request || this.selectedStepStatus() !== 'InProgress' || historyState.status !== 'ready') {
      return null;
    }

      return {
        ...request,
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
  protected readonly historyPaneSummary = computed(() => {
    if (this.historyPanelMode() === 'executions') {
      return this.selectedBlueprintRunCountLabel();
    }

    const count = this.blueprintActivityTotalCount();
    if (count === 0) {
      return 'Recent execution activity';
    }

    return count === 1
      ? '1 blueprint with activity'
      : `${count} blueprints with activity`;
  });
  protected readonly selectedBlueprintTitle = computed(() =>
    this.selectedBlueprint()?.name
    ?? (this.selectedBlueprintId() ? `Blueprint ${this.selectedBlueprintId()}` : 'Execution History'),
  );
  protected readonly selectedBlueprintRunCountLabel = computed(() => {
    const count = this.executionHistoryRows().length;
    if (count === 0) {
      return 'No runs loaded yet';
    }

    if (this.executionHistoryHasMore()) {
      return `${count}+ loaded runs`;
    }

    return count === 1 ? '1 run' : `${count} runs`;
  });
  protected readonly errorMessage = computed(() => {
    const blueprintsState = this.blueprintsState();
    if (isErrorRequestState(blueprintsState)) {
      return blueprintsState.message;
    }

    if (this.historyPanelMode() === 'blueprints' && this.blueprintActivityError()) {
      return this.blueprintActivityError();
    }

    if (this.historyPanelMode() === 'executions' && this.executionHistoryError()) {
      return this.executionHistoryError();
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
        this.selectedExecutionId()
        ?? this.preferredWorkflowInstanceId()
        ?? this.routeSelection().workflowInstanceId
        ?? this.activeExecutionId(),
      );
    });

    effect(() => {
      const execution = this.fetchedExecution();
      if (!execution) {
        return;
      }

      this.centerOnStartingNode(execution);
    });

    effect((onCleanup) => {
      const request = this.stepLogStreamRequest();
      this.stopStepLogStream();

      if (!request) {
        return;
      }

      const eventSource = this.observabilityService.createExecutionStepLogStream(
        request.blueprintDefinitionId,
        request.workflowInstanceId,
        request.blueprintTaskInstanceId,
        {
          from: request.from,
          cursor: request.cursor,
        },
      );

      eventSource.addEventListener('logBatch', (event) => {
        const messageEvent = event as MessageEvent<string>;
        const batch = JSON.parse(messageEvent.data) as NodeLogBatchViewModel;
        this.appendStepLogEntries(batch.entries);
        this.stepLogStreamConnected.set(true);
      });

      eventSource.onerror = () => {
        this.stepLogStreamConnected.set(false);
      };

      this.stepLogEventSource = eventSource;

      onCleanup(() => {
        eventSource.close();
        if (this.stepLogEventSource === eventSource) {
          this.stepLogEventSource = null;
        }
        this.stepLogStreamConnected.set(false);
      });
    });

    toObservable(this.blueprintSearchQuery)
      .pipe(
        tap(() => {
          this.blueprintActivityLoading.set(true);
          this.blueprintActivityLoadingMore.set(false);
          this.blueprintActivityError.set(null);
          this.blueprintActivityRows.set([]);
          this.blueprintActivityPage.set(1);
          this.blueprintActivityTotalCount.set(0);
          this.blueprintActivityHasMore.set(false);
        }),
        switchMap((query) => this.blueprintService.getExecutionBlueprintActivities(query, 1, 10).pipe(
          map((page) => ({ page, error: null as string | null })),
          catchError(() => of({
            page: null,
            error: 'Unable to load blueprint activity.',
          })),
        )),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ page, error }) => {
        this.blueprintActivityLoading.set(false);

        if (error || !page) {
          this.blueprintActivityError.set(error ?? 'Unable to load blueprint activity.');
          return;
        }

        this.blueprintActivityRows.set(page.items);
        this.blueprintActivityPage.set(page.page);
        this.blueprintActivityTotalCount.set(page.totalCount);
        this.blueprintActivityHasMore.set(page.hasMore);
      });

    toObservable(computed(() => {
      const blueprintId = this.selectedBlueprintId();
      if (!blueprintId) {
        return null;
      }

      return {
        blueprintId,
        refreshVersion: this.refreshVersion(),
      };
    }))
      .pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintId === right?.blueprintId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
        tap((request) => {
          this.executionHistoryBaseRows.set([]);
          this.executionHistoryNextCursor.set(null);
          this.executionHistoryHasMoreState.set(false);
          this.executionHistoryError.set(null);
          this.executionHistoryLoadingMore.set(false);
          this.executionHistoryLoading.set(request !== null);
        }),
        switchMap((request) => {
          if (!request) {
            return of({
              page: null,
              error: null as string | null,
            });
          }

          return this.blueprintService.getBlueprintExecutionHistory(request.blueprintId, 25).pipe(
            map((page) => ({ page, error: null as string | null })),
            catchError(() => of({
              page: null,
              error: 'Unable to load execution history for the selected blueprint.',
            })),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ page, error }) => {
        this.executionHistoryLoading.set(false);

        if (error) {
          this.executionHistoryError.set(error);
          return;
        }

        if (!page) {
          return;
        }

        this.executionHistoryBaseRows.set(page.items);
        this.executionHistoryNextCursor.set(page.nextCursor ?? null);
        this.executionHistoryHasMoreState.set(page.hasMore);
      });

    toObservable(this.routeSelection)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((selection) => {
        this.applyBlueprintSelection(
          selection.blueprintId,
          selection.workflowInstanceId,
          selection.blueprintId ? 'executions' : 'blueprints',
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

    toObservable(this.activeExecutionId)
      .pipe(
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.closeExecutionInfo();
        this.closeStepDrawer();
      });

    toObservable(this.stepLogHistoryRequest)
      .pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.workflowInstanceId === right?.workflowInstanceId &&
          left?.blueprintTaskInstanceId === right?.blueprintTaskInstanceId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((request) => {
        this.liveStepLogs.set([]);
        if (!request) {
          this.stopStepLogStream();
        }
      });

    this.executionRealtime.connect();
    this.destroyRef.onDestroy(() => {
      this.stopStepLogStream();
      void this.executionRealtime.disconnect();
    });
  }

  protected toggleHistoryPanel(): void {
    this.historyCollapsed.update((collapsed) => !collapsed);
  }

  protected selectBlueprintActivity(activity: BlueprintExecutionActivityViewModel): void {
    this.applyBlueprintSelection(activity.blueprintDefinitionId, null, 'executions');
  }

  protected returnToBlueprintPicker(): void {
    this.historyPanelMode.set('blueprints');
  }

  protected async loadMoreBlueprintActivities(): Promise<void> {
    if (!this.blueprintActivityHasMore() || this.blueprintActivityLoadingMore()) {
      return;
    }

    const query = this.blueprintSearchQuery();
    const nextPage = this.blueprintActivityPage() + 1;
    this.blueprintActivityLoadingMore.set(true);
    this.blueprintActivityError.set(null);

    try {
      const response = await firstValueFrom(
        this.blueprintService.getExecutionBlueprintActivities(query, nextPage, 10),
      );

      if (query !== this.blueprintSearchQuery() || nextPage <= this.blueprintActivityPage()) {
        return;
      }

      this.blueprintActivityRows.update((currentRows) =>
        this.mergeBlueprintActivities(currentRows, response.items),
      );
      this.blueprintActivityPage.set(response.page);
      this.blueprintActivityTotalCount.set(response.totalCount);
      this.blueprintActivityHasMore.set(response.hasMore);
    } catch {
      if (query === this.blueprintSearchQuery()) {
        this.blueprintActivityError.set('Unable to load more blueprints.');
      }
    } finally {
      this.blueprintActivityLoadingMore.set(false);
    }
  }

  protected selectExecution(execution: BlueprintExecutionHistoryEntryViewModel): void {
    this.closeExecutionInfo();
    this.selectedExecutionId.set(execution.workflowInstanceId);
    this.preferredWorkflowInstanceId.set(null);
  }

  protected onReadonlyNodeActivated(nodeId: number): void {
    const selectedNode = this.executionNodeViewByCanvasId().get(nodeId);
    if (!selectedNode) {
      return;
    }

    if (this.selectedStepInstanceId() !== selectedNode.instanceId) {
      this.stepInspectorTab.set('inputs');
    }

    this.selectedStepInstanceId.set(selectedNode.instanceId);
  }

  protected closeStepDrawer(): void {
    this.selectedStepInstanceId.set(null);
    this.stepInspectorTab.set('inputs');
    this.liveStepLogs.set([]);
    this.stopStepLogStream();
  }

  protected toggleExecutionInfo(event: MouseEvent): void {
    event.stopPropagation();
    this.executionInfoOpen.update((open) => !open);
  }

  protected closeExecutionInfo(): void {
    this.executionInfoOpen.set(false);
  }

  protected selectStepInspectorTab(tab: StepInspectorTab): void {
    this.stepInspectorTab.set(tab);
  }

  protected async loadMoreExecutionHistory(): Promise<void> {
    const blueprintId = this.selectedBlueprintId();
    const cursor = this.executionHistoryNextCursor();

    if (!blueprintId || !cursor || this.executionHistoryLoadingMore()) {
      return;
    }

    this.executionHistoryLoadingMore.set(true);
    this.executionHistoryError.set(null);

    try {
      const response = await firstValueFrom(
        this.blueprintService.getBlueprintExecutionHistory(blueprintId, 25, cursor),
      );

      if (blueprintId !== this.selectedBlueprintId() || cursor !== this.executionHistoryNextCursor()) {
        return;
      }

      this.executionHistoryBaseRows.update((currentRows) =>
        this.mergeExecutionHistoryRows(currentRows, response.items),
      );
      this.executionHistoryNextCursor.set(response.nextCursor ?? null);
      this.executionHistoryHasMoreState.set(response.hasMore);
    } catch {
      if (blueprintId === this.selectedBlueprintId()) {
        this.executionHistoryError.set('Unable to load more executions.');
      }
    } finally {
      this.executionHistoryLoadingMore.set(false);
    }
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

  protected stepLogLabel(entry: NodeLogEntryViewModel): string {
    return entry.serviceName ?? 'Step';
  }

  protected blueprintActivityCountLabel(activity: BlueprintExecutionActivityViewModel): string {
    return activity.recentExecutionCount === 1
      ? '1 recent run'
      : `${activity.recentExecutionCount} recent runs`;
  }

  protected executionIdLabel(execution: BlueprintExecutionHistoryEntryViewModel): string {
    if (execution.workflowInstanceId.length <= 14) {
      return execution.workflowInstanceId;
    }

    return `${execution.workflowInstanceId.slice(0, 8)}...${execution.workflowInstanceId.slice(-4)}`;
  }

  protected trackBlueprintActivity(
    _: number,
    activity: BlueprintExecutionActivityViewModel,
  ): number {
    return activity.blueprintDefinitionId;
  }

  protected trackExecution(
    _: number,
    execution: BlueprintExecutionHistoryEntryViewModel,
  ): string {
    return execution.workflowInstanceId;
  }

  protected trackStepLog(_: number, entry: NodeLogEntryViewModel): string {
    return entry.cursor;
  }

  protected hasStructuredPayload(payload: unknown | null | undefined): boolean {
    return payload !== null && payload !== undefined;
  }

  protected formatStructuredPayload(payload: unknown | null | undefined): string {
    if (!this.hasStructuredPayload(payload)) {
      return '';
    }

    return JSON.stringify(payload, null, 2);
  }

  protected formatDuration(durationMs: number | null | undefined): string {
    if (durationMs === null || durationMs === undefined || Number.isNaN(durationMs)) {
      return 'Not available';
    }

    if (durationMs < 1_000) {
      return `${Math.round(durationMs)}ms`;
    }

    const totalSeconds = durationMs / 1_000;
    if (totalSeconds < 60) {
      return `${totalSeconds.toFixed(totalSeconds < 10 ? 1 : 0)}s`;
    }

    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}m ${seconds.toFixed(seconds < 10 ? 1 : 0)}s`;
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: MouseEvent): void {
    if (!this.executionInfoOpen()) {
      return;
    }

    const target = event.target;
    if (!(target instanceof Node)) {
      this.closeExecutionInfo();
      return;
    }

    const infoButton = this.executionInfoButton()?.nativeElement;
    if (infoButton?.contains(target)) {
      return;
    }

    const infoPopover = this.executionInfoPopover()?.nativeElement;
    if (infoPopover?.contains(target)) {
      return;
    }

    this.closeExecutionInfo();
  }

  @HostListener('document:keydown.escape')
  protected onEscapeKey(): void {
    this.closeExecutionInfo();
  }

  private applyBlueprintSelection(
    blueprintId: number | null,
    preferredWorkflowInstanceId: string | null,
    historyPanelMode: HistoryPanelMode,
  ): void {
    this.closeExecutionInfo();
    this.selectedBlueprintId.set(blueprintId);
    this.preferredWorkflowInstanceId.set(preferredWorkflowInstanceId);
    this.historyPanelMode.set(historyPanelMode);
    this.selectedExecutionId.set(null);
    this.historyCollapsed.set(false);
    this.closeStepDrawer();
    this.realtimeSummaryOverrides.set(new Map());
    this.realtimeNodeOverrides.set(new Map());
  }

  private applyRealtimeSummaryUpdates(updates: ExecutionSummaryUpdatedEvent[]): void {
    const selectedBlueprintId = this.selectedBlueprintId();
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

  private appendStepLogEntries(entries: NodeLogEntryViewModel[]): void {
    if (entries.length === 0) {
      return;
    }

    this.liveStepLogs.update((currentEntries) => {
      const entriesByCursor = new Map(currentEntries.map((entry) => [entry.cursor, entry] as const));
      for (const entry of entries) {
        entriesByCursor.set(entry.cursor, entry);
      }

      return [...entriesByCursor.values()]
        .sort((left, right) => Date.parse(left.timestamp) - Date.parse(right.timestamp));
    });
  }

  private stopStepLogStream(): void {
    this.stepLogEventSource?.close();
    this.stepLogEventSource = null;
    this.stepLogStreamConnected.set(false);
  }

  private mergeBlueprintActivities(
    currentRows: BlueprintExecutionActivityViewModel[],
    nextRows: BlueprintExecutionActivityViewModel[],
  ): BlueprintExecutionActivityViewModel[] {
    const rowsByDefinitionId = new Map<number, BlueprintExecutionActivityViewModel>(
      currentRows.map((row) => [row.blueprintDefinitionId, row] as const),
    );

    for (const row of nextRows) {
      rowsByDefinitionId.set(row.blueprintDefinitionId, row);
    }

    return [...rowsByDefinitionId.values()];
  }

  private mergeExecutionHistoryRows(
    currentRows: BlueprintExecutionHistoryEntryViewModel[],
    nextRows: BlueprintExecutionHistoryEntryViewModel[],
  ): BlueprintExecutionHistoryEntryViewModel[] {
    const rowsByWorkflowId = new Map<string, BlueprintExecutionHistoryEntryViewModel>(
      currentRows.map((row) => [row.workflowInstanceId, row] as const),
    );

    for (const row of nextRows) {
      rowsByWorkflowId.set(row.workflowInstanceId, row);
    }

    return [...rowsByWorkflowId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  }
}
