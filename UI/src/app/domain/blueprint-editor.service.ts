import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ConnectionModel } from './connection-model';
import { NodeModel, Point, Side } from './node-model';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

interface CoordinatesViewModel {
  x: number;
  y: number;
}

interface BlueprintTaskConnectionInputModel {
  channelId?: number | null;
  channelName?: string | null;
  connectedTaskInstanceId: string;
  anchor: number;
  side: Side;
  position: number;
}

interface BlueprintTaskInputModel {
  instanceId: string;
  versionId: number;
  position: CoordinatesViewModel;
  outgoingConnections: BlueprintTaskConnectionInputModel[];
}

interface BlueprintInputModel {
  name: string;
  description?: string | null;
  startingTaskInstanceId: string;
  tasks: BlueprintTaskInputModel[];
}

interface BlueprintTaskConnectionViewModel {
  channelId?: number | null;
  channelName?: string | null;
  taskInstanceId: string;
  anchor: number;
  side: Side;
  position: number;
}

interface BlueprintTaskViewModel {
  instanceId: string;
  versionId: number;
  position: CoordinatesViewModel;
  outgoingConnections: BlueprintTaskConnectionViewModel[];
}

export interface BlueprintVersionViewModel {
  blueprintVersionId: number;
  blueprintDefinitionId: number;
  name: string;
  description?: string | null;
  version: number;
  startingTaskInstanceId: string;
  tasks: BlueprintTaskViewModel[];
}

export interface BlueprintViewModel {
  id: number;
  name: string;
  description: string;
  versions: BlueprintVersionViewModel[];
}

export interface BlueprintExecutionInputModel {
  globalState: Record<string, unknown>;
  initialTaskInput: Record<string, unknown>;
}

export interface BlueprintExecutionAcceptedViewModel {
  workflowInstanceId: string;
  blueprintId: number;
  acceptedAt: string;
}

export interface WorkflowExecutionSummaryViewModel {
  workflowInstanceId: string;
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

export interface WorkflowExecutionNodeViewModel {
  instanceId: string;
  versionId: number;
  name: string;
  status: 'NotExecuted' | 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  position: CoordinatesViewModel;
  failureMessage?: string | null;
  inputChannelName?: string | null;
  outputChannelNames: string[];
  outgoingConnections: BlueprintTaskConnectionViewModel[];
}

export interface WorkflowExecutionDetailViewModel {
  workflowInstanceId: string;
  blueprintDefinitionId: number;
  blueprintName: string;
  blueprintVersionId: number;
  blueprintVersionNumber: number;
  blueprintVersionName: string;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed';
  createdAt: string;
  updatedAt?: string | null;
  startingTaskInstanceId: string;
  nodes: WorkflowExecutionNodeViewModel[];
}

@Injectable({ providedIn: 'root' })
export class BlueprintEditorService {
  readonly nodes = signal<NodeModel[]>([]);
  readonly connections = signal<ConnectionModel[]>([]);
  private readonly http = inject(HttpClient);

  private createNodeModel(
    id: number,
    taskVersionId: number,
    name: string,
    coordinates: Point,
    inputChannelName: string | null,
    outputChannelNames: string[],
    snap?: (v: number) => number,
  ): NodeModel {
    const y = snap ? snap(coordinates.y) : coordinates.y;
    const outputCount = Math.max(outputChannelNames.length, 1);
    return new NodeModel(
      id,
      taskVersionId,
      name,
      snap ? snap(coordinates.x) : coordinates.x,
      y,
      320,
      Math.max(220, 168 + outputCount * 34),
      inputChannelName,
      outputChannelNames,
    );
  }

  createNode(taskDefinition: ITaskDefinition, coordinates: Point, snap: (v: number) => number): NodeModel {
    const version = taskDefinition.versions[0];
    if (!version) {
      throw new Error(`Task definition ${taskDefinition.name} has no versions.`);
    }

    const node = new NodeModel(
      Date.now(),
      version.id,
      taskDefinition.name,
      snap(coordinates.x),
      snap(coordinates.y),
      320,
      Math.max(220, 168 + Math.max(version.outputChannels.length, 1) * 34),
      version.inputChannel?.name ?? null,
      version.outputChannels.map((channel) => channel.name),
    );

    this.nodes.update((n) => [...n, node]);
    return node;
  }

  updateNodeCoordinates(nodeId: number, newCoordinates: Point, snap: (v: number) => number): void {
    const nx = snap(newCoordinates.x);
    const ny = snap(newCoordinates.y);

    this.nodes.update((nodes) =>
      nodes.map((n) => (n.id === nodeId
        ? new NodeModel(
          n.id,
          n.taskVersionId,
          n.name,
          nx,
          ny,
          n.width,
          n.height,
          n.inputChannelName,
          n.outputChannelNames,
        )
        : n)),
    );

    // keep connection endpoints consistent as they reference node instances
    this.rebindConnections();
  }

  createConnection(sourceNodeId: number, destinationNodeId: number, anchor: number, side: Side, position: number, snap: (v: number) => number): void {
    const from = this.getNodeById(sourceNodeId);
    const to = this.getNodeById(destinationNodeId);
    const snappedPosition = snap(position);

    this.connections.update((c) => [...c, new ConnectionModel(from, to, anchor, side, snappedPosition)]);
  }

  getNodeById(id: number): NodeModel {
    return this.nodes().find((n) => n.id === id)!;
  }

  getNodes(...except: Array<number | undefined>): NodeModel[] {
    return this.nodes().filter((node) => !except.includes(node.id));
  }

  getNodeByCoordinates(coordinates: Point, ...except: NodeModel[]): NodeModel | undefined {
    return this.nodes().find((node) => {
      if (except.includes(node) || except.some((n) => n.contains(coordinates.x, coordinates.y))) {
        return false;
      }

      return node.contains(coordinates.x, coordinates.y);
    });
  }

  getNodesConnectedTo(node: NodeModel): NodeModel[] {
    return [...new Set(
      this.connections()
        .filter((c) => c.from === node || c.to === node)
        .flatMap((c) => [c.from, c.to])
        .filter((n) => !!n),
    )];
  }

  private rebindConnections(): void {
    const nodeMap = new Map(this.nodes().map((n) => [n.id, n] as const));
    this.connections.update((connections) =>
      connections.map((c) => new ConnectionModel(nodeMap.get(c.from.id)!, nodeMap.get(c.to.id)!, c.anchor, c.side, c.pos)),
    );
  }

  clear(): void {
    this.nodes.set([]);
    this.connections.set([]);
  }

  getBlueprints(): Promise<BlueprintViewModel[]> {
    return this.http.get<BlueprintViewModel[]>('api/blueprints').toPromise() as Promise<BlueprintViewModel[]>;
  }

  getBlueprintById(definitionId: number): Promise<BlueprintViewModel> {
    return this.http.get<BlueprintViewModel>(`api/blueprints/${definitionId}`).toPromise() as Promise<BlueprintViewModel>;
  }

  saveBlueprint(name: string, startingNodeId: number, taskDefinitions: ITaskDefinition[]): Promise<BlueprintViewModel> {
    const instanceIdByNodeId = new Map<number, string>(this.nodes().map((n) => [n.id, crypto.randomUUID()]));

    const tasks: BlueprintTaskInputModel[] = this.nodes().map((n) => ({
      instanceId: instanceIdByNodeId.get(n.id)!,
      versionId: n.taskVersionId,
      position: { x: n.x, y: n.y },
      outgoingConnections: [],
    }));

    const taskByNodeId = new Map<number, BlueprintTaskInputModel>(this.nodes().map((n, index) => [n.id, tasks[index]]));
    const taskDefinitionByVersionId = new Map<number, ITaskDefinition['versions'][number]>();
    for (const taskDefinition of taskDefinitions) {
      for (const version of taskDefinition.versions) {
        taskDefinitionByVersionId.set(version.id, version);
      }
    }

    for (const c of this.connections()) {
      const source = taskByNodeId.get(c.from.id);
      if (!source) continue;

      const sourceTaskVersion = taskDefinitionByVersionId.get(source.versionId);
      const anchorChannelName = c.from.outputChannelNameForAnchor(c.anchor);
      const defaultOutputChannel = sourceTaskVersion?.outputChannels.find((channel) => channel.name === anchorChannelName)
        ?? sourceTaskVersion?.outputChannels[0];
      source.outgoingConnections.push({
        channelId: null,
        channelName: defaultOutputChannel?.name ?? null,
        connectedTaskInstanceId: instanceIdByNodeId.get(c.to.id)!,
        anchor: c.anchor,
        side: c.side,
        position: c.pos,
      });
    }

    const payload: BlueprintInputModel = {
      name,
      description: null,
      startingTaskInstanceId: instanceIdByNodeId.get(startingNodeId)!,
      tasks,
    };

    return this.http.post<BlueprintViewModel>('api/blueprints', payload).toPromise() as Promise<BlueprintViewModel>;
  }

  async loadBlueprint(definitionId: number, taskDefinitions: ITaskDefinition[]): Promise<BlueprintViewModel> {
    const blueprint = await this.getBlueprintById(definitionId);

    const version = blueprint.versions[0];
    if (!version) {
      this.clear();
      return blueprint;
    }

    const taskNameByVersionId = new Map<number, string>();
    const taskVersionById = new Map<number, ITaskDefinition['versions'][number]>();
    for (const taskDefinition of taskDefinitions) {
      for (const taskVersion of taskDefinition.versions) {
        taskNameByVersionId.set(taskVersion.id, taskDefinition.name);
        taskVersionById.set(taskVersion.id, taskVersion);
      }
    }

    const nodes = version.tasks.map((task, index) => {
      const taskVersion = taskVersionById.get(task.versionId);
      return this.createNodeModel(
        index + 1,
        task.versionId,
        taskNameByVersionId.get(task.versionId) ?? `Task ${task.versionId}`,
        task.position,
        taskVersion?.inputChannel?.name ?? null,
        taskVersion?.outputChannels.map((channel) => channel.name) ?? [],
      );
    });

    const nodeIdByInstanceId = new Map<string, number>(version.tasks.map((task, index) => [task.instanceId, index + 1]));
    const nodeById = new Map<number, NodeModel>(nodes.map((node) => [node.id, node]));

    const connections = version.tasks.flatMap((task) =>
      task.outgoingConnections.flatMap((connection) => {
        const fromNodeId = nodeIdByInstanceId.get(task.instanceId);
        const toNodeId = nodeIdByInstanceId.get(connection.taskInstanceId);
        if (!fromNodeId || !toNodeId) {
          return [];
        }

        return [new ConnectionModel(
          nodeById.get(fromNodeId)!,
          nodeById.get(toNodeId)!,
          connection.anchor,
          connection.side,
          connection.position,
        )];
      }),
    );

    this.nodes.set(nodes);
    this.connections.set(connections);
    return blueprint;
  }

  executeBlueprint(definitionId: number, payload: BlueprintExecutionInputModel): Promise<BlueprintExecutionAcceptedViewModel> {
    return this.http.post<BlueprintExecutionAcceptedViewModel>(`api/blueprints/${definitionId}/execute`, payload).toPromise() as Promise<BlueprintExecutionAcceptedViewModel>;
  }

  getBlueprintExecutions(definitionId: number): Promise<WorkflowExecutionSummaryViewModel[]> {
    return this.http.get<WorkflowExecutionSummaryViewModel[]>(`api/blueprints/${definitionId}/executions`).toPromise() as Promise<WorkflowExecutionSummaryViewModel[]>;
  }

  getBlueprintExecution(definitionId: number, workflowInstanceId: string): Promise<WorkflowExecutionDetailViewModel> {
    return this.http.get<WorkflowExecutionDetailViewModel>(`api/blueprints/${definitionId}/executions/${workflowInstanceId}`).toPromise() as Promise<WorkflowExecutionDetailViewModel>;
  }
}
