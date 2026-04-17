import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe, NgClass } from '@angular/common';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink, RouterLinkActive } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatToolbarModule } from '@angular/material/toolbar';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  BlueprintEditorService,
  BlueprintViewModel,
  WorkflowExecutionDetailViewModel,
  WorkflowExecutionNodeViewModel,
  WorkflowExecutionSummaryViewModel,
} from '../domain/blueprint-editor.service';
import { NodeModel } from '../domain/node-model';
import { ConnectionModel } from '../domain/connection-model';

type ExecutionNodeStatus =
  | 'NotExecuted'
  | 'Pending'
  | 'InProgress'
  | 'Completed'
  | 'Failed';

interface ExecutionNavItem {
  label: string;
  route: string;
  exact?: boolean;
}

interface ExecutionGraphNode {
  data: WorkflowExecutionNodeViewModel;
  model: NodeModel;
}

interface ExecutionGraphConnection {
  key: string;
  segments: Array<{ x1: number; y1: number; x2: number; y2: number }>;
  status: ExecutionNodeStatus;
}

const EXECUTION_NAV_ITEMS: ExecutionNavItem[] = [
  { label: 'Workflows', route: '/blueprints', exact: true },
  { label: 'Trigger', route: '/execute' },
  { label: 'Executions', route: '/executions' },
];

@Component({
  selector: 'app-workflow-executions-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    RouterLinkActive,
    MatToolbarModule,
    MatButtonModule,
    MatIconModule,
    NgClass,
    DatePipe,
  ],
  templateUrl: './workflow-executions-page.component.html',
  styleUrl: './workflow-executions-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowExecutionsPageComponent implements AfterViewInit {
  private readonly blueprintService = inject(BlueprintEditorService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly graphViewportRef = viewChild<ElementRef<HTMLDivElement>>('graphViewport');
  private requestedBlueprintId: number | null = null;
  private requestedWorkflowInstanceId: string | null = null;
  private readonly preferredWorkflowInstanceId = signal<string | null>(null);
  private lastLoadedBlueprintId: number | null = null;

  protected readonly navItems = EXECUTION_NAV_ITEMS;
  protected readonly loading = signal(true);
  protected readonly pickerOpen = signal(false);
  protected readonly executionsLoading = signal(false);
  protected readonly detailLoading = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly blueprints = signal<BlueprintViewModel[]>([]);
  protected readonly selectedBlueprint = signal<BlueprintViewModel | null>(null);
  protected readonly executionSummaries = signal<WorkflowExecutionSummaryViewModel[]>([]);
  protected readonly selectedExecution = signal<WorkflowExecutionDetailViewModel | null>(null);
  protected readonly selectedExecutionId = signal<string | null>(null);
  protected readonly blueprintSearch = new FormControl('', { nonNullable: true });
  protected readonly graphWidth = signal(1200);
  protected readonly graphHeight = signal(720);

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

  protected readonly graphNodes = computed<ExecutionGraphNode[]>(() => {
    const detail = this.selectedExecution();
    if (!detail) {
      return [];
    }

    return detail.nodes.map((node, index) => ({
      data: node,
      model: new NodeModel(
        index + 1,
        node.versionId,
        node.name,
        node.position.x,
        node.position.y,
        320,
        Math.max(220, 168 + Math.max(node.outputChannelNames.length, 1) * 34),
        node.inputChannelName ?? null,
        node.outputChannelNames,
      ),
    }));
  });

  protected readonly graphConnections = computed<ExecutionGraphConnection[]>(() => {
    const nodes = this.graphNodes();
    if (nodes.length === 0) {
      return [];
    }

    const nodeByInstanceId = new Map(nodes.map((node) => [node.data.instanceId, node] as const));
    const nodeModels = nodes.map((node) => node.model);
    const options = {
      gridSize: 20,
      nodeMargin: NodeModel.buffer,
      obstacleBuffer: Math.max(NodeModel.buffer / 20, 1),
    };

    return nodes.flatMap((node) =>
      node.data.outgoingConnections.flatMap((connection) => {
        const target = nodeByInstanceId.get(connection.taskInstanceId);
        if (!target) {
          return [];
        }

        const model = new ConnectionModel(
          node.model,
          target.model,
          connection.anchor,
          connection.side,
          connection.position,
        );
        const obstacles = nodeModels.filter((candidate) =>
          candidate.id !== node.model.id && candidate.id !== target.model.id,
        );

        return [{
          key: `${node.data.instanceId}-${connection.taskInstanceId}-${connection.anchor}-${connection.position}`,
          segments: model.segments(options, obstacles),
          status: node.data.status,
        }];
      }),
    );
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

    void this.loadBlueprints();
  }

  ngAfterViewInit(): void {
    queueMicrotask(() => this.updateGraphBounds());
  }

  protected readonly executionCountLabel = computed(() => {
    const count = this.executionSummaries().length;
    return count === 1 ? '1 execution' : `${count} executions`;
  });

  protected selectBlueprint(blueprint: BlueprintViewModel): void {
    void this.openBlueprintExecutions(blueprint);
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
    this.pickerOpen.set(false);
    this.selectedExecution.set(null);
    this.selectedExecutionId.set(null);
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
      this.updateGraphBounds();
      this.scrollGraphToStart(detail.startingTaskInstanceId);
    } catch {
      this.errorMessage.set('Unable to load the selected execution.');
    } finally {
      this.detailLoading.set(false);
    }
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

  protected statusLabel(status: string): string {
    switch (status) {
      case 'Completed':
        return 'Succeeded';
      case 'InProgress':
        return 'In Progress';
      case 'Failed':
        return 'Failed';
      case 'Pending':
        return 'Pending';
      default:
        return 'Not Executed';
    }
  }

  protected trackBlueprint(_: number, blueprint: BlueprintViewModel): number {
    return blueprint.id;
  }

  protected trackExecution(_: number, execution: WorkflowExecutionSummaryViewModel): string {
    return execution.workflowInstanceId;
  }

  protected trackGraphNode(_: number, node: ExecutionGraphNode): string {
    return node.data.instanceId;
  }

  protected nodeTooltip(node: WorkflowExecutionNodeViewModel): string | null {
    if (node.status !== 'Failed') {
      return null;
    }

    return node.failureMessage?.trim()
      || 'This node failed, but no persisted error details were available for this execution.';
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

  private updateGraphBounds(): void {
    const nodes = this.graphNodes();
    if (nodes.length === 0) {
      this.graphWidth.set(1200);
      this.graphHeight.set(720);
      return;
    }

    const maxX = Math.max(...nodes.map((node) => node.model.x + node.model.width));
    const maxY = Math.max(...nodes.map((node) => node.model.y + node.model.height));
    this.graphWidth.set(Math.max(1200, maxX + 240));
    this.graphHeight.set(Math.max(720, maxY + 180));
  }

  private scrollGraphToStart(startingTaskInstanceId: string): void {
    const viewport = this.graphViewportRef()?.nativeElement;
    const startNode = this.graphNodes().find((node) => node.data.instanceId === startingTaskInstanceId);
    if (!viewport || !startNode) {
      return;
    }

    viewport.scrollTo({
      left: Math.max(startNode.model.x - 140, 0),
      top: Math.max(startNode.model.y - 80, 0),
      behavior: 'smooth',
    });
  }
}
