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
}

interface BlueprintTaskViewModel {
  instanceId: string;
  versionId: number;
  position: CoordinatesViewModel;
  outgoingConnections: BlueprintTaskConnectionViewModel[];
}

interface BlueprintVersionViewModel {
  blueprintVersionId: number;
  blueprintDefinitionId: number;
  name: string;
  description?: string | null;
  version: number;
  startingTaskInstanceId: number;
  tasks: BlueprintTaskViewModel[];
}

interface BlueprintViewModel {
  id: number;
  name: string;
  description: string;
  versions: BlueprintVersionViewModel[];
}

@Injectable({ providedIn: 'root' })
export class BlueprintEditorService {
  readonly nodes = signal<NodeModel[]>([]);
  readonly connections = signal<ConnectionModel[]>([]);
  private readonly http = inject(HttpClient);

  createNode(name: string, taskVersionId: number, coordinates: Point, snap: (v: number) => number): NodeModel {
    const node = new NodeModel(
      Date.now(),
      taskVersionId,
      name,
      snap(coordinates.x),
      snap(coordinates.y),
      240,
      160,
    );

    this.nodes.update((n) => [...n, node]);
    return node;
  }

  updateNodeCoordinates(nodeId: number, newCoordinates: Point, snap: (v: number) => number): void {
    const nx = snap(newCoordinates.x);
    const ny = snap(newCoordinates.y);

    this.nodes.update((nodes) =>
      nodes.map((n) => (n.id === nodeId ? new NodeModel(n.id, n.taskVersionId, n.name, nx, ny, n.width, n.height) : n)),
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
      const defaultOutputChannel = sourceTaskVersion?.outputChannels[0];
      source.outgoingConnections.push({
        channelId: null,
        channelName: defaultOutputChannel?.name ?? null,
        connectedTaskInstanceId: instanceIdByNodeId.get(c.to.id)!,
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

  async loadBlueprint(definitionId: number): Promise<BlueprintViewModel> {
    const blueprint = await (this.http.get<BlueprintViewModel>(`api/blueprints/${definitionId}`).toPromise() as Promise<BlueprintViewModel>);

    // current API returns metadata + versions list; task graph load endpoint is version-based and not yet wired.
    // Clear canvas for now.
    this.clear();
    return blueprint;
  }
}
