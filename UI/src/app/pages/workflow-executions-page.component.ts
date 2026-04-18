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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
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
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly canvasViewport = viewChild(WorkflowCanvasComponent);
  private requestedBlueprintId: number | null = null;
  private requestedWorkflowInstanceId: string | null = null;
  private readonly preferredWorkflowInstanceId = signal<string | null>(null);
  private lastLoadedBlueprintId: number | null = null;

  protected readonly loading = signal(true);
  protected readonly pickerOpen = signal(false);
  protected readonly executionsLoading = signal(false);
  protected readonly detailLoading = signal(false);
  protected readonly historyCollapsed = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly selectedBlueprint = signal<BlueprintViewModel | null>(null);
  protected readonly executionSummaries = signal<WorkflowExecutionSummaryViewModel[]>([]);
  protected readonly selectedExecution = signal<WorkflowExecutionDetailViewModel | null>(null);
  protected readonly selectedExecutionId = signal<string | null>(null);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly executionSearch = new FormControl('', { nonNullable: true });
  protected readonly zoomPercent = computed(() => this.canvasViewport()?.zoomPercent() ?? 100);

  protected readonly filteredBlueprints = computed(() => {
    const query = this.blueprintSearch.value.trim().toLocaleLowerCase();
    const blueprints = [...this.blueprints()].sort((left, right) => left.name.localeCompare(right.name));
    if (!query) {
      return blueprints;
    }

    return blueprints.filter((blueprint) =>
      blueprint.name.toLocaleLowerCase().includes(query),
    );
  });

  protected readonly filteredExecutions = computed(() => {
    const query = this.executionSearch.value.trim().toLocaleLowerCase();
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
      const blueprintId = this.selectedBlueprint()?.id ?? null;
      const preferredWorkflowInstanceId = this.preferredWorkflowInstanceId();
      if (!blueprintId) {
        return;
      }

      if (blueprintId === this.lastLoadedBlueprintId && !preferredWorkflowInstanceId) {
        return;
      }

      this.lastLoadedBlueprintId = blueprintId;
      void this.loadExecutions(blueprintId, preferredWorkflowInstanceId);
      this.preferredWorkflowInstanceId.set(null);
    });

    effect(() => {
      this.executionRealtime.setSubscriptions(
        this.selectedBlueprint()?.id ?? null,
        this.selectedExecutionId(),
      );
    });

    this.blueprintSearch.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.pickerOpen.set(true);
      });

    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((params) => {
        const blueprintId = Number(params.get('blueprintId'));
        this.requestedBlueprintId = Number.isInteger(blueprintId) && blueprintId > 0
          ? blueprintId
          : null;
        this.requestedWorkflowInstanceId = params.get('workflowInstanceId');

        if (!this.loading()) {
          void this.restoreRouteSelection();
        }
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
        void this.refreshCurrentSelection();
      });

    this.executionRealtime.connect();
    this.destroyRef.onDestroy(() => {
      void this.executionRealtime.disconnect();
    });

    void this.loadBlueprints();
  }

  protected toggleHistoryPanel(): void {
    this.historyCollapsed.update((collapsed) => !collapsed);
  }

  protected async selectBlueprint(blueprint: BlueprintViewModel): Promise<void> {
    await this.openBlueprintExecutions(blueprint);
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

  protected async selectExecution(execution: WorkflowExecutionSummaryViewModel): Promise<void> {
    const blueprint = this.selectedBlueprint();
    if (!blueprint) {
      return;
    }

    this.selectedExecutionId.set(execution.workflowInstanceId);
    this.detailLoading.set(true);
    this.errorMessage.set(null);
    try {
      const detail = await this.blueprintService.getBlueprintExecution(
        blueprint.id,
        execution.workflowInstanceId,
      );
      this.selectedExecution.set(detail);
      this.centerOnStartingNode(detail);
    } catch {
      this.errorMessage.set('Unable to load the selected execution.');
    } finally {
      this.detailLoading.set(false);
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

  protected trackBlueprint(_: number, blueprint: BlueprintViewModel): number {
    return blueprint.id;
  }

  protected trackExecution(_: number, execution: WorkflowExecutionSummaryViewModel): string {
    return execution.workflowInstanceId;
  }

  private async openBlueprintExecutions(
    blueprint: BlueprintViewModel,
    preferredWorkflowInstanceId?: string | null,
  ): Promise<void> {
    if (this.selectedBlueprint()?.id !== blueprint.id) {
      this.lastLoadedBlueprintId = null;
    }

    this.preferredWorkflowInstanceId.set(preferredWorkflowInstanceId ?? null);
    this.selectedBlueprint.set(blueprint);
    this.blueprintSearch.setValue(blueprint.name, { emitEvent: false });
    this.executionSearch.setValue('', { emitEvent: false });
    this.pickerOpen.set(false);
    this.selectedExecution.set(null);
    this.selectedExecutionId.set(null);
    this.historyCollapsed.set(false);
  }

  private async loadBlueprints(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const blueprints = await this.blueprintService.getBlueprints();
      this.blueprints.set(blueprints);
      await this.restoreRouteSelection();
    } catch {
      this.errorMessage.set('Unable to load blueprints.');
    } finally {
      this.loading.set(false);
    }
  }

  private async loadExecutions(
    blueprintId: number,
    preferredWorkflowInstanceId?: string | null,
  ): Promise<void> {
    this.executionsLoading.set(true);
    this.executionSummaries.set([]);
    this.errorMessage.set(null);
    try {
      const executions = await this.blueprintService.getBlueprintExecutions(blueprintId);
      this.executionSummaries.set(executions);

      const selectedExecution = preferredWorkflowInstanceId
        ? executions.find((execution) => execution.workflowInstanceId === preferredWorkflowInstanceId) ?? executions[0]
        : executions[0];

      if (selectedExecution) {
        await this.selectExecution(selectedExecution);
      }
    } catch {
      this.errorMessage.set('Unable to load executions for the selected blueprint.');
    } finally {
      this.executionsLoading.set(false);
    }
  }

  private async restoreRouteSelection(): Promise<void> {
    if (!this.requestedBlueprintId) {
      return;
    }

    const blueprint = this.blueprints().find((candidate) => candidate.id === this.requestedBlueprintId);
    if (!blueprint) {
      return;
    }

    const alreadySelected = this.selectedBlueprint()?.id === blueprint.id;
    if (alreadySelected && !this.requestedWorkflowInstanceId) {
      return;
    }

    await this.openBlueprintExecutions(blueprint, this.requestedWorkflowInstanceId);
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

    this.executionSummaries.update((currentSummaries) => {
      const summariesByWorkflowId = new Map(currentSummaries.map((summary) => [summary.workflowInstanceId, summary] as const));

      for (const update of relevantUpdates) {
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

      return [...summariesByWorkflowId.values()]
        .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
    });

    const selectedExecutionId = this.selectedExecutionId();
    if (!selectedExecutionId) {
      return;
    }

    const matchingSummary = relevantUpdates.find((update) => update.workflowInstanceId === selectedExecutionId);
    if (!matchingSummary) {
      return;
    }

    this.selectedExecution.update((currentExecution) => currentExecution
      ? {
        ...currentExecution,
        blueprintVersionId: matchingSummary.blueprintVersionId,
        blueprintVersionNumber: matchingSummary.blueprintVersionNumber,
        blueprintVersionName: matchingSummary.blueprintVersionName,
        status: matchingSummary.status,
        updatedAt: matchingSummary.updatedAt ?? currentExecution.updatedAt,
      }
      : currentExecution);
  }

  private applyRealtimeNodeUpdates(updates: ExecutionNodesUpdatedEvent[]): void {
    const selectedExecution = this.selectedExecution();
    if (!selectedExecution) {
      return;
    }

    const relevantUpdates = updates.filter((update) => update.workflowInstanceId === selectedExecution.workflowInstanceId);
    if (relevantUpdates.length === 0) {
      return;
    }

    const deltasByInstanceId = new Map(relevantUpdates
      .flatMap((update) => update.nodes)
      .map((node) => [node.instanceId, node] as const));

    this.selectedExecution.update((currentExecution) => currentExecution
      ? {
        ...currentExecution,
        nodes: currentExecution.nodes.map((node) => {
          const delta = deltasByInstanceId.get(node.instanceId);
          return delta
            ? {
              ...node,
              status: delta.status,
              resolvedVersionId: delta.resolvedVersionId,
              resolvedVersion: delta.resolvedVersion ?? node.resolvedVersion,
              failureMessage: delta.failureMessage ?? null,
            }
            : node;
        }),
      }
      : currentExecution);
  }

  private async refreshCurrentSelection(): Promise<void> {
    const blueprintId = this.selectedBlueprint()?.id;
    if (!blueprintId) {
      return;
    }

    try {
      const executions = await this.blueprintService.getBlueprintExecutions(blueprintId);
      this.executionSummaries.set(executions);

      const selectedExecutionId = this.selectedExecutionId();
      if (!selectedExecutionId) {
        return;
      }

      const detail = await this.blueprintService.getBlueprintExecution(
        blueprintId,
        selectedExecutionId,
      );
      this.selectedExecution.set(detail);
    } catch {
      // Best-effort refresh after reconnect.
    }
  }

  private centerOnStartingNode(detail: WorkflowExecutionDetailViewModel): void {
    const nodeIndex = detail.nodes.findIndex((node) => node.instanceId === detail.startingTaskInstanceId);
    if (nodeIndex < 0) {
      return;
    }

    queueMicrotask(() => this.canvasViewport()?.centerOnNode(nodeIndex + 1));
  }
}
