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
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
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
  BlueprintProcessActivityViewModel,
  BlueprintProcessHistoryEntryViewModel,
  BlueprintViewModel,
  ProcessDetailViewModel,
  ProcessSearchRequest,
  ProcessStepDetailViewModel,
  ProcessStepViewModel,
} from '../domain/blueprint-editor.service';
import {
  ProcessStepsUpdatedEvent,
  ProcessRealtimeService,
  ProcessSummaryUpdatedEvent,
} from '../domain/process-realtime.service';
import { NodeModel } from '../domain/node-model';
import { ConnectionModel } from '../domain/connection-model';
import { BlueprintCanvasComponent } from '../components/blueprint-canvas.component';
import type {
  BlueprintCanvasEvent,
  BlueprintCanvasGraph,
  BlueprintCanvasRuntime,
  BlueprintCanvasSelection,
} from '../components/blueprint-canvas.component';
import {
  ProcessStepLogBatchViewModel,
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
import { LogEventStream } from '../domain/log-event-stream';
import { TaskDefinitionsService } from '../domain/task-definitions.service';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';
import { ProcessSearchDialogComponent } from '../components/process-search-dialog.component';
import type { RecoveryDialogData } from '../components/process-recovery-dialog.component';
import { summarizeConditionalRule } from '../domain/conditional-rule';
import { buildProcessCanvasNodes } from '../domain/process-canvas-model';
import { PhaseSeriesExplorerService, PhaseSeriesOverview, PhaseSeriesPhase } from '../domain/phase-series-explorer.service';

interface ProcessRouteSelection {
  blueprintId: number | null;
  processId: string | null;
}

interface StepLogHistoryRequest {
  blueprintDefinitionId: number;
  processId: string;
  blueprintNodeId: string;
  from: string;
  to: string | null;
}

type HistoryPanelMode = 'blueprints' | 'processes' | 'search-results';
type StepInspectorTab = 'inputs' | 'outputs' | 'dataflow' | 'logs';

interface ProcessStepStatusCounts {
  total: number;
  completed: number;
  skipped: number;
  failed: number;
  running: number;
  pending: number;
}

@Component({
  selector: 'app-process-explorer-page',
  imports: [
    RouterLink,
    ReactiveFormsModule,
    MatButtonModule,
    MatChipsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatMenuModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatSidenavModule,
    MatTabsModule,
    MatTooltipModule,
    NgClass,
    DatePipe,
    BlueprintCanvasComponent,
  ],
  templateUrl: './process-explorer-page.component.html',
  styleUrl: './process-explorer-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProcessExplorerPageComponent {
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly phaseSeriesService = inject(PhaseSeriesExplorerService);
  private readonly dialog = inject(MatDialog);
  private readonly processRealtime = inject(ProcessRealtimeService);
  private readonly observabilityService = inject(ObservabilityService);
  private readonly runtimeClientConfig = inject(RuntimeClientConfigService);
  private readonly taskDefinitionsService = inject(TaskDefinitionsService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  protected readonly parentNavigation = toSignal(this.route.queryParamMap.pipe(map(params => {
    const definitionId = Number(params.get('parentDefinitionId'));
    const parentId = params.get('parentId');
    return {
      requestedLink: Number.isSafeInteger(definitionId) && definitionId > 0 && parentId
        ? ['/processes/phase-series', definitionId, parentId] : null,
      search: { q: params.get('q'), status: params.get('status') },
    };
  })));
  private readonly canvasViewport = viewChild(BlueprintCanvasComponent);
  private readonly refreshVersion = signal(0);
  private readonly preferredProcessId = signal<string | null>(null);
  private readonly realtimeSummaryOverrides = signal(new Map<string, ProcessSummaryUpdatedEvent>());
  private readonly realtimeNodes = signal<{
    processId: string;
    byInstance: ReadonlyMap<string, ProcessStepsUpdatedEvent['nodes'][number]>;
  } | null>(null);
  private readonly realtimeNodeOverrides = computed(() => {
    const update = this.realtimeNodes();
    return update?.processId === this.fetchedProcess()?.processId ? update?.byInstance : undefined;
  });
  private readonly liveStepLogs = signal<NodeLogEntryViewModel[]>([]);
  private stepLogEventSource: LogEventStream | null = null;
  private readonly routeSelection = toSignal(
    this.route.queryParamMap.pipe(
      map((params): ProcessRouteSelection => {
        const blueprintId = Number(params.get('blueprintId'));
        return {
          blueprintId: Number.isInteger(blueprintId) && blueprintId > 0 ? blueprintId : null,
          processId: params.get('processId'),
        };
      }),
      distinctUntilChanged((left, right) =>
        left.blueprintId === right.blueprintId &&
        left.processId === right.processId,
      ),
    ),
    {
      initialValue: {
        blueprintId: null,
        processId: null,
      } satisfies ProcessRouteSelection,
    },
  );

  protected readonly historyDrawerOpen = signal(false);
  protected readonly historyPanelMode = signal<HistoryPanelMode>('blueprints');
  protected readonly selectedBlueprintId = signal<number | null>(null);
  protected readonly selectedProcessId = signal<string | null>(null);
  protected readonly selectedStepInstanceId = signal<string | null>(null);
  protected readonly stepInspectorTab = signal<StepInspectorTab>('inputs');
  protected readonly stepLogStreamConnected = signal(false);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly taskDefinitions = toSignal(this.taskDefinitionsService.getTaskDefinitions(), { initialValue: [] as ITaskDefinition[] });
  protected readonly activeSearchRequest = signal<ProcessSearchRequest | null>(null);
  protected readonly recoveryEnabled = this.runtimeClientConfig.processRecoveryEnabled;
  protected readonly selectAllForRecovery = signal(false);
  protected readonly recoverySelection = signal<ReadonlySet<string>>(new Set());
  private readonly searchResultRows = signal<BlueprintProcessHistoryEntryViewModel[]>([]);
  private readonly searchResultsNextCursor = signal<string | null>(null);
  private readonly searchResultsHasMoreState = signal(false);
  protected readonly searchResultsLoading = signal(false);
  protected readonly searchResultsLoadingMore = signal(false);
  protected readonly searchResultsError = signal<string | null>(null);

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

  private readonly blueprintActivityRows = signal<BlueprintProcessActivityViewModel[]>([]);
  private readonly blueprintActivityPage = signal(1);
  private readonly blueprintActivityTotalCount = signal(0);
  protected readonly blueprintActivityHasMore = signal(false);
  protected readonly blueprintActivityLoading = signal(false);
  protected readonly blueprintActivityLoadingMore = signal(false);
  protected readonly blueprintActivityError = signal<string | null>(null);

  private readonly processHistoryBaseRows = signal<BlueprintProcessHistoryEntryViewModel[]>([]);
  private readonly processHistoryNextCursor = signal<string | null>(null);
  private readonly processHistoryHasMoreState = signal(false);
  protected readonly processHistoryLoading = signal(false);
  protected readonly processHistoryLoadingMore = signal(false);
  protected readonly processHistoryError = signal<string | null>(null);

  protected readonly loading = computed(() => this.blueprintsState().status === 'loading');
  protected readonly blueprints = computed<BlueprintViewModel[]>(() => {
    const state = this.blueprintsState();
    return isReadyRequestState(state) ? state.data : [];
  });
  protected readonly selectedBlueprint = computed<BlueprintViewModel | null>(() =>
    this.blueprints().find((candidate) => candidate.id === this.selectedBlueprintId()) ?? null,
  );
  protected readonly blueprintActivities = computed(() => this.blueprintActivityRows());
  protected readonly processHistoryRows = computed<BlueprintProcessHistoryEntryViewModel[]>(() => {
    const selectedBlueprintId = this.selectedBlueprintId();
    const rowsByProcessId = new Map<string, BlueprintProcessHistoryEntryViewModel>(
      this.processHistoryBaseRows().map((row) => [row.processId, row] as const),
    );

    if (selectedBlueprintId !== null) {
      for (const update of this.realtimeSummaryOverrides().values()) {
        if (update.blueprintDefinitionId !== selectedBlueprintId) {
          continue;
        }

        const existingRow = rowsByProcessId.get(update.processId);
        rowsByProcessId.set(update.processId, {
          processId: update.processId,
          blueprintDefinitionId: update.blueprintDefinitionId,
          blueprintName: update.blueprintName,
          blueprintVersionId: update.blueprintVersionId,
          blueprintVersionNumber: update.blueprintVersionNumber,
          status: update.status,
          createdAt: existingRow?.createdAt ?? update.createdAt,
          updatedAt: update.updatedAt ?? existingRow?.updatedAt ?? null,
        });
      }
    }

    return [...rowsByProcessId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  });
  protected readonly processHistoryHasMore = computed(() => this.processHistoryHasMoreState());
  protected readonly searchResultsRows = computed(() => this.searchResultRows());
  protected readonly searchResultsHasMore = computed(() => this.searchResultsHasMoreState());
  protected readonly activeProcessId = computed(() => {
    const currentProcessId = this.selectedProcessId();
    if (currentProcessId) {
      return currentProcessId;
    }

    const preferredProcessIdValue = this.preferredProcessId();
    if (preferredProcessIdValue) {
      return preferredProcessIdValue;
    }

    if (this.historyPanelMode() === 'search-results') {
      return null;
    }

    return this.processHistoryRows()[0]?.processId ?? null;
  });
  private readonly selectedProcessState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const blueprintId = this.selectedBlueprintId();
        const processId = this.activeProcessId();
        if (!blueprintId || !processId) {
          return null;
        }

        return {
          blueprintId,
          processId,
          refreshVersion: this.refreshVersion(),
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintId === right?.blueprintId &&
          left?.processId === right?.processId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
      ),
      ({ blueprintId, processId }) =>
        this.blueprintService.getBlueprintProcess(blueprintId, processId),
      'Unable to load the selected process.',
    ),
    { initialValue: idleRequestState<ProcessDetailViewModel>() },
  );
  protected readonly detailLoading = computed(() => this.selectedProcessState().status === 'loading');
  private readonly fetchedProcess = computed<ProcessDetailViewModel | null>(() => {
    const state = this.selectedProcessState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly selectedProcess = computed<ProcessDetailViewModel | null>(() => {
    const process = this.fetchedProcess();
    if (!process) {
      return null;
    }

    const summaryOverride = this.realtimeSummaryOverrides().get(process.processId);
    const nodeOverrides = this.realtimeNodeOverrides();

    return {
      ...process,
      blueprintName: summaryOverride?.blueprintName ?? process.blueprintName,
      blueprintVersionId: summaryOverride?.blueprintVersionId ?? process.blueprintVersionId,
      blueprintVersionNumber: summaryOverride?.blueprintVersionNumber ?? process.blueprintVersionNumber,
      status: summaryOverride?.status ?? process.status,
      updatedAt: summaryOverride?.updatedAt ?? process.updatedAt,
      nodes: process.nodes.map((node) => {
        const delta = nodeOverrides?.get(node.instanceId);
        if (!delta) {
          return node;
        }

        return {
          ...node,
          status: delta.status,
          selectedChannel: delta.selectedChannel === undefined ? node.selectedChannel : delta.selectedChannel,
          selectedChannels: delta.iterations?.length
            ? [...new Set(delta.iterations.flatMap(item => item.selectedChannel ? [item.selectedChannel] : []))]
            : node.selectedChannels,
          iterations: delta.iterations ?? node.iterations,
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
  private readonly selectedPhasedProcessState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const phase = this.selectedProcess()?.phase;
        if (!phase) {
          return null;
        }

        return {
          phasedBlueprintDefinitionId: phase.phasedBlueprintDefinitionId,
          phasedProcessId: phase.phasedProcessId,
          refreshVersion: this.refreshVersion(),
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.phasedBlueprintDefinitionId === right?.phasedBlueprintDefinitionId &&
          left?.phasedProcessId === right?.phasedProcessId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
      ),
      ({ phasedBlueprintDefinitionId, phasedProcessId }) =>
        this.phaseSeriesService.overview(phasedBlueprintDefinitionId, phasedProcessId),
      'Unable to load the Phase Series overview.',
    ),
    { initialValue: idleRequestState<PhaseSeriesOverview>() },
  );
  protected readonly phasedProcessLoading = computed(() => this.selectedPhasedProcessState().status === 'loading');
  protected readonly selectedPhasedProcess = computed<PhaseSeriesOverview | null>(() => {
    const state = this.selectedPhasedProcessState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly selectedPhasedProcessError = computed(() => {
    const state = this.selectedPhasedProcessState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly selectedProcessPhaseIndex = computed(() => this.selectedProcess()?.phase?.phaseIndex ?? null);
  protected readonly selectedProcessStepCounts = computed<ProcessStepStatusCounts>(() => {
    const nodes = this.selectedProcess()?.nodes ?? [];
    return this.countStepStatuses(nodes);
  });
  protected readonly selectedProcessProgressLabel = computed(() => {
    const counts = this.selectedProcessStepCounts();
    if (counts.total === 0) {
      return 'No steps';
    }

    return `${counts.completed}/${counts.total - counts.skipped} steps completed`;
  });
  protected readonly selectedProcessLastActivityLabel = computed(() => {
    const process = this.selectedProcess();
    if (!process) {
      return 'No process selected';
    }

    return process.updatedAt ? 'Updated' : 'Created';
  });
  private readonly processNodeViewByCanvasId = computed(() =>
    new Map(
      (this.selectedProcess()?.nodes ?? []).map((node, index) => [index + 1, node] as const),
    ),
  );
  private readonly processCanvasNodeIdByInstanceId = computed(() =>
    new Map(
      (this.selectedProcess()?.nodes ?? []).map((node, index) => [node.instanceId, index + 1] as const),
    ),
  );
  protected readonly selectedProcessCanvasNodeId = computed(() => {
    const selectedStepInstanceId = this.selectedStepInstanceId();
    if (!selectedStepInstanceId) {
      return null;
    }

    return this.processCanvasNodeIdByInstanceId().get(selectedStepInstanceId) ?? null;
  });
  protected readonly selectedProcessStepNode = computed(() => {
    const selectedStepInstanceId = this.selectedStepInstanceId();
    if (!selectedStepInstanceId) {
      return null;
    }

    return this.selectedProcess()?.nodes.find((node) => node.instanceId === selectedStepInstanceId) ?? null;
  });
  protected readonly stepDrawerOpen = computed(() => this.selectedStepInstanceId() !== null);
  private readonly selectedStepState = toSignal(
    createRequestStateStream(
      toObservable(computed(() => {
        const process = this.selectedProcess();
        const blueprintNodeId = this.selectedStepInstanceId();
        if (!process || !blueprintNodeId) {
          return null;
        }

        return {
          blueprintDefinitionId: process.blueprintDefinitionId,
          processId: process.processId,
          blueprintNodeId,
          refreshVersion: this.refreshVersion(),
        };
      })).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.processId === right?.processId &&
          left?.blueprintNodeId === right?.blueprintNodeId &&
          left?.refreshVersion === right?.refreshVersion,
        ),
      ),
      ({ blueprintDefinitionId, processId, blueprintNodeId }) =>
        this.blueprintService.getProcessStepDetail(
          blueprintDefinitionId,
          processId,
          blueprintNodeId,
        ),
      'Unable to load the selected step.',
    ),
    { initialValue: idleRequestState<ProcessStepDetailViewModel>() },
  );
  protected readonly stepDetailLoading = computed(() => this.selectedStepState().status === 'loading');
  protected readonly selectedStepDetail = computed<ProcessStepDetailViewModel | null>(() => {
    const state = this.selectedStepState();
    return isReadyRequestState(state) ? state.data : null;
  });
  protected readonly stepDetailError = computed(() => {
    const state = this.selectedStepState();
    return isErrorRequestState(state) ? state.message : null;
  });
  protected readonly conditionalSummary = computed(() => {
    const rule = this.selectedProcessStepNode()?.conditionalRule ?? this.selectedStepDetail()?.conditionalRule;
    return rule ? summarizeConditionalRule(rule, true, this.selectedProcessStepNode()?.conditionalFields ?? this.selectedStepDetail()?.conditionalFields) : null;
  });
  protected readonly chosenChannel = computed(() => this.selectedProcessStepNode()?.selectedChannel ?? this.selectedStepDetail()?.selectedChannel);
  protected readonly decisionIterations = computed(() =>
    this.realtimeNodeOverrides()?.get(this.selectedStepInstanceId() ?? '')?.iterations
    ?? this.selectedStepDetail()?.iterations ?? this.selectedProcessStepNode()?.iterations ?? []);
  protected readonly selectedStepTitle = computed(() =>
    this.selectedStepDetail()?.name
    ?? this.selectedProcessStepNode()?.name
    ?? 'Step details',
  );
  protected readonly selectedStepStatus = computed(() =>
    this.realtimeNodeOverrides()?.get(this.selectedStepInstanceId() ?? '')?.status
    ?? this.selectedStepDetail()?.status
    ?? this.selectedProcessStepNode()?.status
    ?? 'NotExecuted',
  );
  protected readonly selectedStepIsConditional = computed(() =>
    (this.selectedProcessStepNode()?.nodeKind ?? this.selectedStepDetail()?.nodeKind) === 'ConditionalBranch',
  );
  protected readonly selectedStepResolvedVersion = computed(() =>
    this.selectedStepDetail()?.resolvedVersion
    ?? this.selectedProcessStepNode()?.resolvedVersion
    ?? null,
  );
  protected readonly selectedStepStartedAt = computed(() =>
    this.selectedStepDetail()?.startedAt
    ?? this.selectedProcessStepNode()?.startedAt
    ?? null,
  );
  protected readonly selectedStepEndedAt = computed(() =>
    this.selectedStepDetail()?.endedAt
    ?? this.selectedProcessStepNode()?.failedAt
    ?? this.selectedProcessStepNode()?.completedAt
    ?? null,
  );
  protected readonly selectedStepDurationMs = computed(() =>
    this.selectedStepDetail()?.durationMs ?? null,
  );
  protected readonly selectedStepFailureMessage = computed(() =>
    this.selectedStepDetail()?.failureMessage
    ?? this.selectedProcessStepNode()?.failureMessage
    ?? null,
  );
  protected readonly selectedStepInputs = computed(() => this.selectedStepDetail()?.inputs ?? null);
  protected readonly selectedStepOutputs = computed(() => this.selectedStepDetail()?.outputs ?? null);
  protected readonly selectedStepIncomingMappings = computed(() => this.selectedStepDetail()?.incomingMappings ?? []);
  protected readonly selectedStepDesignTimeOverrides = computed(() =>
    this.selectedStepDetail()?.designTimeInputOverrides ?? null,
  );
  protected readonly selectedStepRuntimeOverrides = computed(() =>
    this.selectedStepDetail()?.runtimeOverrides ?? null,
  );
  protected readonly selectedStepSummaryLine = computed(() => {
    const version = this.selectedStepResolvedVersion();
    const logCount = this.stepLogCount();
    const versionLabel = this.selectedStepIsConditional() ? 'Native Step'
      : version ? `Version ${version}` : 'Version not resolved';
    const logLabel = logCount === 1 ? '1 log entry' : `${logCount} log entries`;

    const stepId = this.decisionIterations().length > 1 ? null : this.selectedStepDetail()?.runtimeStepId;
    return `${this.recoveryStatusLabel(stepId, this.selectedStepStatus())} · ${versionLabel} · ${logLabel}`;
  });
  private readonly stepLogHistoryRequest = computed<StepLogHistoryRequest | null>(() => {
    const process = this.selectedProcess();
    const blueprintNodeId = this.selectedStepInstanceId();
    if (!process || !blueprintNodeId || this.stepInspectorTab() !== 'logs') {
      return null;
    }

    const startedAt = this.selectedStepStartedAt() ?? process.createdAt;
    const status = this.selectedStepStatus();

    return {
      blueprintDefinitionId: process.blueprintDefinitionId,
      processId: process.processId,
      blueprintNodeId,
      from: startedAt,
      to: status === 'InProgress' ? null : this.selectedStepEndedAt() ?? process.updatedAt ?? null,
    };
  });
  private readonly stepLogHistoryState = toSignal(
    createRequestStateStream(
      toObservable(this.stepLogHistoryRequest).pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.processId === right?.processId &&
          left?.blueprintNodeId === right?.blueprintNodeId &&
          left?.from === right?.from &&
          left?.to === right?.to,
        ),
      ),
      (request) => this.observabilityService.getProcessStepLogs(
        request.blueprintDefinitionId,
        request.processId,
        request.blueprintNodeId,
        {
          from: request.from,
          to: request.to,
          limit: 400,
        },
      ),
      'Unable to load logs for that step.',
    ),
    { initialValue: idleRequestState<ProcessStepLogBatchViewModel>() },
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
  protected readonly processCanvasGraph = computed<BlueprintCanvasGraph>(() => ({
    nodes: this.processNodes(),
    connections: this.processConnections(),
    revision: this.refreshVersion(),
  }));
  protected readonly processCanvasRuntime = computed<BlueprintCanvasRuntime>(() => ({
    mode: 'readonly',
    backgroundEffect: 'stitch',
    connectionPresentation: 'normal',
    viewportInsets: { top: 0, right: 0, bottom: 0, left: 0 },
  }));
  protected readonly processCanvasSelection = computed<BlueprintCanvasSelection>(() => ({
    selectedNodeIds: [],
    selectedConnectionIds: [],
    readonlySelectedNodeId: this.selectedProcessCanvasNodeId(),
    canvasFocused: false,
    modifiers: { shift: false, ctrlOrMeta: false, space: false },
  }));
  protected readonly selectedProcessGrafanaLogsUrl = computed(() => {
    const process = this.selectedProcess();
    if (!process) {
      return this.runtimeClientConfig.grafanaBaseUrl;
    }

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'loki',
      queries: [
        {
          refId: 'A',
          expr: `{service_name=~".+"} | process_id="${process.processId}"`,
        },
      ],
      range: {
        from: process.createdAt,
        to: process.updatedAt,
      },
    }));

    return `${this.runtimeClientConfig.grafanaBaseUrl}/explore?orgId=1&left=${left}`;
  });
  protected readonly selectedProcessGrafanaTraceUrl = computed(() => {
    const process = this.selectedProcess();
    if (!process) {
      return this.runtimeClientConfig.grafanaBaseUrl;
    }

    const left = encodeURIComponent(JSON.stringify({
      datasource: 'tempo',
      queries: [
        {
          refId: 'A',
          query: process.processId,
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
    if (this.historyPanelMode() === 'search-results') {
      return this.searchResultsCountLabel();
    }

    if (this.historyPanelMode() === 'processes') {
      return this.selectedBlueprintProcessCountLabel();
    }

    const count = this.blueprintActivityTotalCount();
    if (count === 0) {
      return 'Recent process activity';
    }

    return count === 1
      ? '1 blueprint with activity'
      : `${count} blueprints with activity`;
  });
  protected readonly historyPaneEyebrow = computed(() => {
    switch (this.historyPanelMode()) {
      case 'search-results':
        return 'Advanced Search';
      case 'processes':
        return 'Blueprint Processes';
      default:
        return 'Process Explorer';
    }
  });
  protected readonly selectedBlueprintTitle = computed(() =>
    this.selectedBlueprint()?.name
    ?? (this.selectedBlueprintId() ? `Blueprint ${this.selectedBlueprintId()}` : 'Process History'),
  );
  protected readonly selectedBlueprintProcessCountLabel = computed(() => {
    const count = this.processHistoryRows().length;
    if (count === 0) {
      return 'No processes loaded yet';
    }

    if (this.processHistoryHasMore()) {
      return `${count}+ loaded processes`;
    }

    return count === 1 ? '1 process' : `${count} processes`;
  });
  protected readonly searchResultsCountLabel = computed(() => {
    const count = this.searchResultsRows().length;
    if (count === 0) {
      return this.searchResultsLoading() ? 'Searching processes' : 'No matches';
    }

    if (this.searchResultsHasMore()) {
      return `${count}+ matching processes`;
    }

    return count === 1 ? '1 matching process' : `${count} matching processes`;
  });
  protected readonly activeSearchSummaryChips = computed(() => {
    const request = this.activeSearchRequest();
    if (!request) {
      return [];
    }

    const chips = [this.searchWindowLabel(request)];
    if (request.blueprintDefinitionIds.length > 0) {
      chips.push(request.blueprintDefinitionIds.length === 1 ? '1 blueprint' : `${request.blueprintDefinitionIds.length} blueprints`);
    }
    const blueprintStatusSelections = request.blueprintFilters?.reduce((total, filter) => total + filter.statuses.length, 0) ?? 0;
    if (blueprintStatusSelections > 0) {
      chips.push(blueprintStatusSelections === 1 ? '1 status selection' : `${blueprintStatusSelections} status selections`);
    }
    if (request.nodeFilters.length > 0) {
      chips.push(request.nodeFilters.length === 1 ? '1 node filter' : `${request.nodeFilters.length} node filters`);
    }
    const nodeStatusSelections = request.nodeFilters.reduce((total, filter) => total + filter.statuses.length, 0);
    if (nodeStatusSelections > 0) {
      chips.push(nodeStatusSelections === 1 ? '1 step status' : `${nodeStatusSelections} step statuses`);
    }
    if (request.propertyFilters.length > 0) {
      chips.push(request.propertyFilters.length === 1 ? '1 property filter' : `${request.propertyFilters.length} property filters`);
    }
    if (request.processStatuses.length > 0) {
      chips.push(request.processStatuses.length === 1 ? '1 status' : `${request.processStatuses.length} statuses`);
    }
    return chips;
  });
  protected readonly errorMessage = computed(() => {
    const blueprintsState = this.blueprintsState();
    if (isErrorRequestState(blueprintsState)) {
      return blueprintsState.message;
    }

    if (this.historyPanelMode() === 'blueprints' && this.blueprintActivityError()) {
      return this.blueprintActivityError();
    }

    if (this.historyPanelMode() === 'search-results' && this.searchResultsError()) {
      return this.searchResultsError();
    }

    if (this.historyPanelMode() === 'processes' && this.processHistoryError()) {
      return this.processHistoryError();
    }

    const selectedProcessState = this.selectedProcessState();
    if (isErrorRequestState(selectedProcessState)) {
      return selectedProcessState.message;
    }

    const selectedPhasedProcessState = this.selectedPhasedProcessState();
    if (isErrorRequestState(selectedPhasedProcessState)) {
      return selectedPhasedProcessState.message;
    }

    return null;
  });

  protected readonly processNodes = computed(() => {
    const detail = this.selectedProcess();
    if (!detail) {
      return [];
    }

    return buildProcessCanvasNodes(detail, this.taskDefinitions());
  });

  protected readonly processConnections = computed(() => {
    const detail = this.selectedProcess();
    const nodes = this.processNodes();
    if (!detail || nodes.length === 0) {
      return [];
    }

    const nodeByInstanceId = new Map(detail.nodes.map((node, index) => [node.instanceId, nodes[index]] as const));
    return detail.nodes.flatMap((node) => {
      const fromNode = nodeByInstanceId.get(node.instanceId);
      if (!fromNode) {
        return [];
      }

      const chosenChannels = node.selectedChannel ? [node.selectedChannel] : node.selectedChannels;
      return node.outgoingConnections.flatMap((connection) => {
        const toNode = nodeByInstanceId.get(connection.blueprintNodeId);
        if (!toNode) {
          return [];
        }

        const channel = connection.channelName ?? fromNode.outputChannelNameForAnchor(connection.anchor);
        const pathNotTaken = node.status === 'Completed' && !!channel &&
          !!chosenChannels?.length && !chosenChannels.includes(channel);
        return [new ConnectionModel(
          fromNode,
          toNode,
          connection.anchor,
          connection.side,
          connection.position,
          crypto.randomUUID(),
          connection.propertyMappings,
          pathNotTaken,
        )];
      });
    });
  });

  constructor() {
    effect(() => {
      this.processRealtime.setSubscriptions(
        this.selectedBlueprintId() ?? this.routeSelection().blueprintId,
        this.selectedProcessId()
        ?? this.preferredProcessId()
        ?? this.routeSelection().processId
        ?? this.activeProcessId(),
      );
    });

    effect(() => {
      const process = this.fetchedProcess();
      if (!process) {
        return;
      }

      this.centerOnStartingNode(process);
    });

    effect((onCleanup) => {
      const request = this.stepLogStreamRequest();
      this.stopStepLogStream();

      if (!request) {
        return;
      }

      const eventSource = this.observabilityService.createProcessStepLogStream(
        request.blueprintDefinitionId,
        request.processId,
        request.blueprintNodeId,
        {
          from: request.from,
          cursor: request.cursor,
        },
      );

      eventSource.addEventListener('logBatch', (event) => {
        const messageEvent = event as MessageEvent<string>;
        const batch = JSON.parse(messageEvent.data) as ProcessStepLogBatchViewModel;
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
        switchMap((query) => this.blueprintService.getProcessBlueprintActivities(query, 1, 10).pipe(
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
          this.processHistoryBaseRows.set([]);
          this.processHistoryNextCursor.set(null);
          this.processHistoryHasMoreState.set(false);
          this.processHistoryError.set(null);
          this.processHistoryLoadingMore.set(false);
          this.processHistoryLoading.set(request !== null);
        }),
        switchMap((request) => {
          if (!request) {
            return of({
              page: null,
              error: null as string | null,
            });
          }

          return this.blueprintService.getBlueprintProcessHistory(request.blueprintId, 25).pipe(
            map((page) => ({ page, error: null as string | null })),
            catchError(() => of({
              page: null,
              error: 'Unable to load process history for the selected blueprint.',
            })),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ page, error }) => {
        this.processHistoryLoading.set(false);

        if (error) {
          this.processHistoryError.set(error);
          return;
        }

        if (!page) {
          return;
        }

        this.processHistoryBaseRows.set(page.items);
        this.processHistoryNextCursor.set(page.nextCursor ?? null);
        this.processHistoryHasMoreState.set(page.hasMore);
      });

    toObservable(this.routeSelection)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((selection) => {
        this.applyBlueprintSelection(
          selection.blueprintId,
          selection.processId,
          selection.blueprintId ? 'processes' : 'blueprints',
        );
      });

    this.processRealtime.summaryUpdates$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((updates) => this.applyRealtimeSummaryUpdates(updates));

    this.processRealtime.nodeUpdates$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((updates) => this.applyRealtimeNodeUpdates(updates));

    this.processRealtime.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.realtimeNodes.set(null);
        this.realtimeSummaryOverrides.set(new Map());
        this.refreshVersion.update((current) => current + 1);
      });

    toObservable(this.activeProcessId)
      .pipe(
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.closeStepDrawer();
      });

    toObservable(this.stepLogHistoryRequest)
      .pipe(
        distinctUntilChanged((left, right) =>
          left?.blueprintDefinitionId === right?.blueprintDefinitionId &&
          left?.processId === right?.processId &&
          left?.blueprintNodeId === right?.blueprintNodeId &&
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

    this.processRealtime.connect();
    this.destroyRef.onDestroy(() => {
      this.stopStepLogStream();
      void this.processRealtime.disconnect();
    });
  }

  protected openHistoryDrawer(): void {
    this.historyDrawerOpen.set(true);
  }

  protected closeHistoryDrawer(): void {
    this.historyDrawerOpen.set(false);
  }

  protected toggleHistoryDrawer(): void {
    this.historyDrawerOpen.update((open) => !open);
  }

  protected selectBlueprintActivity(activity: BlueprintProcessActivityViewModel): void {
    this.applyBlueprintSelection(activity.blueprintDefinitionId, null, 'processes');
  }

  protected async openAdvancedSearch(): Promise<void> {
    const dialogRef = this.dialog.open(ProcessSearchDialogComponent, {
      width: 'min(72rem, calc(100vw - 1.5rem))',
      maxWidth: '100vw',
      ariaLabel: 'Advanced search',
      autoFocus: false,
      data: {
        blueprints: this.blueprints(),
        taskDefinitions: this.taskDefinitions(),
        initialRequest: this.activeSearchRequest(),
      },
    });

    const result = await firstValueFrom(dialogRef.afterClosed());
    if (!result) {
      return;
    }

    await this.applyAdvancedSearch(result);
  }

  protected selectAllMatches(enabled: boolean): void {
    this.selectAllForRecovery.set(enabled);
    this.recoverySelection.set(new Set());
  }

  protected toggleRecoveryRow(processId: string): void {
    this.recoverySelection.update(current => {
      const next = new Set(current);
      if (next.has(processId)) next.delete(processId);
      else next.add(processId);
      return next;
    });
  }

  protected recoverSearch(): void {
    const search = this.activeSearchRequest();
    const allMatches = this.selectAllForRecovery();
    const ids = [...this.recoverySelection()];
    if (!search || (!allMatches && !ids.length)) return;
    void this.openRecovery({ selection: { search, allMatches, ...(allMatches ? { excludedProcessIds: ids } : { selectedProcessIds: ids }) } });
  }

  protected recoverStep(runtimeStepId: string): void {
    const processId = this.activeProcessId();
    if (processId && runtimeStepId) void this.openRecovery({ selection: { processId, runtimeStepId } });
  }

  protected async openRecovery(data: RecoveryDialogData): Promise<void> {
    if (!this.recoveryEnabled) return;
    const { ProcessRecoveryDialogComponent } = await import('../components/process-recovery-dialog.component');
    const dialog = this.dialog.open(ProcessRecoveryDialogComponent, {
      data, width: 'min(58rem, calc(100vw - 1rem))', maxWidth: '100vw',
      maxHeight: '92dvh', autoFocus: false, ariaLabel: 'Step recovery',
    });
    await firstValueFrom(dialog.afterClosed());
    this.refreshVersion.update(value => value + 1);
  }

  protected returnToBlueprintPicker(): void {
    this.historyPanelMode.set('blueprints');
    this.openHistoryDrawer();
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
        this.blueprintService.getProcessBlueprintActivities(query, nextPage, 10),
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

  protected selectProcess(process: BlueprintProcessHistoryEntryViewModel): void {
    if (this.historyPanelMode() === 'search-results') {
      this.selectedBlueprintId.set(process.blueprintDefinitionId);
    }
    this.selectedProcessId.set(process.processId);
    this.preferredProcessId.set(null);
    this.closeHistoryDrawer();
  }

  protected onReadonlyNodeActivated(nodeId: number): void {
    const selectedNode = this.processNodeViewByCanvasId().get(nodeId);
    if (!selectedNode) {
      return;
    }

    if (this.selectedStepInstanceId() !== selectedNode.instanceId) {
      this.stepInspectorTab.set('inputs');
    }

    this.selectedStepInstanceId.set(selectedNode.instanceId);
  }

  protected onCanvasEvent(event: BlueprintCanvasEvent): void {
    if (event.kind === 'readonlyNodeActivated') {
      this.onReadonlyNodeActivated(event.nodeId);
    }
  }

  protected closeStepDrawer(): void {
    this.selectedStepInstanceId.set(null);
    this.stepInspectorTab.set('inputs');
    this.liveStepLogs.set([]);
    this.stopStepLogStream();
  }

  protected selectStepInspectorTab(tab: StepInspectorTab): void {
    this.stepInspectorTab.set(tab);
  }

  protected stepTabIndex(): number {
    switch (this.stepInspectorTab()) {
      case 'outputs':
        return 1;
      case 'dataflow':
        return 2;
      case 'logs':
        return 3;
      default:
        return 0;
    }
  }

  protected onStepTabIndexChange(index: number): void {
    switch (index) {
      case 1:
        this.stepInspectorTab.set('outputs');
        break;
      case 2:
        this.stepInspectorTab.set('dataflow');
        break;
      case 3:
        this.stepInspectorTab.set('logs');
        break;
      default:
        this.stepInspectorTab.set('inputs');
        break;
    }
  }

  protected async loadMoreProcessHistory(): Promise<void> {
    const blueprintId = this.selectedBlueprintId();
    const cursor = this.processHistoryNextCursor();

    if (!blueprintId || !cursor || this.processHistoryLoadingMore()) {
      return;
    }

    this.processHistoryLoadingMore.set(true);
    this.processHistoryError.set(null);

    try {
      const response = await firstValueFrom(
        this.blueprintService.getBlueprintProcessHistory(blueprintId, 25, cursor),
      );

      if (blueprintId !== this.selectedBlueprintId() || cursor !== this.processHistoryNextCursor()) {
        return;
      }

      this.processHistoryBaseRows.update((currentRows) =>
        this.mergeProcessHistoryRows(currentRows, response.items),
      );
      this.processHistoryNextCursor.set(response.nextCursor ?? null);
      this.processHistoryHasMoreState.set(response.hasMore);
    } catch {
      if (blueprintId === this.selectedBlueprintId()) {
        this.processHistoryError.set('Unable to load more processes.');
      }
    } finally {
      this.processHistoryLoadingMore.set(false);
    }
  }

  protected async loadMoreSearchResults(): Promise<void> {
    const request = this.activeSearchRequest();
    const cursor = this.searchResultsNextCursor();

    if (!request || !cursor || this.searchResultsLoadingMore()) {
      return;
    }

    this.searchResultsLoadingMore.set(true);
    this.searchResultsError.set(null);

    try {
      const response = await firstValueFrom(this.blueprintService.searchProcesses({
        ...request,
        cursor,
      }));

      if (request !== this.activeSearchRequest() || cursor !== this.searchResultsNextCursor()) {
        return;
      }

      this.searchResultRows.update((currentRows) =>
        this.mergeProcessHistoryRows(currentRows, response.items),
      );
      this.searchResultsNextCursor.set(response.nextCursor ?? null);
      this.searchResultsHasMoreState.set(response.hasMore);
    } catch {
      if (request === this.activeSearchRequest()) {
        this.searchResultsError.set('Unable to load more search results.');
      }
    } finally {
      this.searchResultsLoadingMore.set(false);
    }
  }

  protected clearAdvancedSearch(): void {
    this.resetSearchResultsState();
    this.historyPanelMode.set(this.selectedBlueprintId() ? 'processes' : 'blueprints');
    this.openHistoryDrawer();
  }

  protected resetView(): void {
    this.canvasViewport()?.viewport.reset();
  }

  protected zoomIn(): void {
    this.canvasViewport()?.viewport.zoomIn();
  }

  protected zoomOut(): void {
    this.canvasViewport()?.viewport.zoomOut();
  }

  protected statusLabel(status: string): string {
    switch (status) {
      case 'Skipped':
        return 'Path not taken';
      case 'Completed':
        return 'Succeeded';
      case 'InProgress':
        return 'Running';
      case 'WaitingForTrigger':
        return 'Waiting for trigger';
      case 'Failed':
        return 'Failed';
      case 'Pending':
        return 'Pending';
      default:
        return 'Not Executed';
    }
  }

  protected recoveryStatusLabel(runtimeStepId: string | null | undefined, status: string): string {
    const attempt = this.selectedStepDetail()?.attempts?.find(item => item.runtimeStepId === runtimeStepId && item.status === status);
    if (attempt?.recoveryAction === 'Skip') return 'Operator skipped';
    if (attempt?.recoveryAction === 'Reuse') return 'Recorded outcome accepted';
    return this.statusLabel(status);
  }

  protected statusIcon(status: string): string {
    switch (status) {
      case 'Skipped':
        return 'skip_next';
      case 'Completed':
        return 'check_circle';
      case 'InProgress':
        return 'hourglass_top';
      case 'WaitingForTrigger':
        return 'notifications_active';
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

  protected blueprintActivityCountLabel(activity: BlueprintProcessActivityViewModel): string {
    return activity.recentProcessCount === 1
      ? '1 recent process'
      : `${activity.recentProcessCount} recent processes`;
  }

  protected processIdLabel(process: BlueprintProcessHistoryEntryViewModel): string {
    return this.compactProcessId(process.processId);
  }

  protected compactProcessId(processId: string): string {
    if (processId.length <= 14) {
      return processId;
    }

    return `${processId.slice(0, 8)}...${processId.slice(-4)}`;
  }

  protected trackBlueprintActivity(
    _: number,
    activity: BlueprintProcessActivityViewModel,
  ): number {
    return activity.blueprintDefinitionId;
  }

  protected trackProcess(
    _: number,
    process: BlueprintProcessHistoryEntryViewModel,
  ): string {
    return process.processId;
  }

  protected trackPhasedProcessPhase(
    _: number,
    phase: PhaseSeriesPhase,
  ): string {
    return phase.phaseId;
  }

  protected selectPhaseProcess(phase: PhaseSeriesPhase): void {
    if (!phase.childProcessId) {
      return;
    }

    this.selectedBlueprintId.set(phase.blueprintDefinitionId);
    this.selectedProcessId.set(phase.childProcessId);
    this.preferredProcessId.set(null);
    this.closeHistoryDrawer();
  }

  protected trackStepLog(_: number, entry: NodeLogEntryViewModel): string {
    return entry.cursor;
  }

  protected trackIncomingMapping(_: number, mapping: ProcessStepDetailViewModel['incomingMappings'][number]): string {
    return `${mapping.sourceBlueprintNodeId}:${mapping.channelName ?? 'default'}`;
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

  protected stepCountSummary(counts: ProcessStepStatusCounts): string {
    if (counts.total === 0) {
      return 'No task state captured yet';
    }

    const parts = [
      counts.running > 0 ? `${counts.running} running` : null,
      counts.failed > 0 ? `${counts.failed} failed` : null,
      counts.pending > 0 ? `${counts.pending} waiting` : null,
      counts.skipped > 0 ? `${counts.skipped} skipped` : null,
    ].filter((part): part is string => part !== null);

    return parts.length > 0 ? parts.join(' · ') : 'All task steps have settled';
  }

  protected searchWindowLabel(request: ProcessSearchRequest): string {
    const formatter = new Intl.DateTimeFormat('en-GB', {
      day: '2-digit',
      month: 'short',
      hour: '2-digit',
      minute: '2-digit',
    });

    return `${formatter.format(new Date(request.createdFrom))} → ${formatter.format(new Date(request.createdTo))}`;
  }

  private applyBlueprintSelection(
    blueprintId: number | null,
    preferredProcessId: string | null,
    historyPanelMode: HistoryPanelMode,
  ): void {
    this.selectedBlueprintId.set(blueprintId);
    this.preferredProcessId.set(preferredProcessId);
    this.historyPanelMode.set(historyPanelMode);
    this.selectedProcessId.set(null);
    this.historyDrawerOpen.set(blueprintId !== null && preferredProcessId === null);
    this.closeStepDrawer();
    this.realtimeSummaryOverrides.set(new Map());
    this.realtimeNodes.set(null);
    if (historyPanelMode !== 'search-results') {
      this.resetSearchResultsState();
    }
  }

  private async applyAdvancedSearch(request: ProcessSearchRequest): Promise<void> {
    this.selectAllMatches(false);
    this.closeStepDrawer();
    this.openHistoryDrawer();
    this.searchResultsLoading.set(true);
    this.searchResultsLoadingMore.set(false);
    this.searchResultsError.set(null);
    this.searchResultRows.set([]);
    this.searchResultsNextCursor.set(null);
    this.searchResultsHasMoreState.set(false);
    this.selectedProcessId.set(null);
    this.preferredProcessId.set(null);
    this.selectedBlueprintId.set(null);
    this.historyPanelMode.set('search-results');
    this.activeSearchRequest.set(request);

    try {
      const response = await firstValueFrom(this.blueprintService.searchProcesses(request));
      if (request !== this.activeSearchRequest()) {
        return;
      }

      this.searchResultRows.set(response.items);
      this.searchResultsNextCursor.set(response.nextCursor ?? null);
      this.searchResultsHasMoreState.set(response.hasMore);
    } catch {
      if (request === this.activeSearchRequest()) {
        this.searchResultsError.set('Unable to search processes with the selected filters.');
      }
    } finally {
      if (request === this.activeSearchRequest()) {
        this.searchResultsLoading.set(false);
      }
    }
  }

  private resetSearchResultsState(): void {
    this.selectAllMatches(false);
    this.activeSearchRequest.set(null);
    this.searchResultRows.set([]);
    this.searchResultsNextCursor.set(null);
    this.searchResultsHasMoreState.set(false);
    this.searchResultsLoading.set(false);
    this.searchResultsLoadingMore.set(false);
    this.searchResultsError.set(null);
  }

  private applyRealtimeSummaryUpdates(updates: ProcessSummaryUpdatedEvent[]): void {
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
        nextOverrides.set(update.processId, update);
      }
      return nextOverrides;
    });
  }

  private applyRealtimeNodeUpdates(updates: ProcessStepsUpdatedEvent[]): void {
    const activeProcessId = this.activeProcessId();
    if (!activeProcessId) {
      return;
    }

    const relevantUpdates = updates.filter((update) => update.processId === activeProcessId);
    if (relevantUpdates.length === 0) {
      return;
    }

    this.realtimeNodes.update((currentUpdate) => {
      const nodes = new Map(currentUpdate?.processId === activeProcessId ? currentUpdate.byInstance : undefined);
      for (const update of relevantUpdates) {
        for (const node of update.nodes) {
          nodes.set(node.instanceId, node);
        }
      }
      return { processId: activeProcessId, byInstance: nodes };
    });
  }

  private centerOnStartingNode(detail: ProcessDetailViewModel): void {
    const nodeIndex = detail.nodes.findIndex((node) => node.instanceId === detail.startingBlueprintNodeId);
    if (nodeIndex < 0) {
      return;
    }

    queueMicrotask(() => this.canvasViewport()?.viewport.centerOnNode(nodeIndex + 1));
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
    currentRows: BlueprintProcessActivityViewModel[],
    nextRows: BlueprintProcessActivityViewModel[],
  ): BlueprintProcessActivityViewModel[] {
    const rowsByDefinitionId = new Map<number, BlueprintProcessActivityViewModel>(
      currentRows.map((row) => [row.blueprintDefinitionId, row] as const),
    );

    for (const row of nextRows) {
      rowsByDefinitionId.set(row.blueprintDefinitionId, row);
    }

    return [...rowsByDefinitionId.values()];
  }

  private mergeProcessHistoryRows(
    currentRows: BlueprintProcessHistoryEntryViewModel[],
    nextRows: BlueprintProcessHistoryEntryViewModel[],
  ): BlueprintProcessHistoryEntryViewModel[] {
    const rowsByProcessId = new Map<string, BlueprintProcessHistoryEntryViewModel>(
      currentRows.map((row) => [row.processId, row] as const),
    );

    for (const row of nextRows) {
      rowsByProcessId.set(row.processId, row);
    }

    return [...rowsByProcessId.values()]
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
  }

  private countStepStatuses(nodes: ProcessStepViewModel[]): ProcessStepStatusCounts {
    return nodes.reduce<ProcessStepStatusCounts>((counts, node) => {
      counts.total += 1;
      switch (node.status) {
        case 'Skipped':
          counts.skipped += 1;
          break;
        case 'Completed':
          counts.completed += 1;
          break;
        case 'Failed':
          counts.failed += 1;
          break;
        case 'InProgress':
          counts.running += 1;
          break;
        default:
          counts.pending += 1;
          break;
      }

      return counts;
    }, {
      total: 0,
      completed: 0,
      skipped: 0,
      failed: 0,
      running: 0,
      pending: 0,
    });
  }
}

