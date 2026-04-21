import { Injectable, computed, signal } from '@angular/core';
import { ConnectionModel, ConnectionPropertyMappingModel } from './connection-model';
import { NodeModel, Point } from './node-model';
import { ITaskDefinition } from '../interfaces/ITaskDefinition';

interface GraphNodeSnapshot {
  id: number;
  taskDefinitionId: number;
  allowedMajor: number;
  allowedMinor: number;
  resolvedVersionId: number;
  resolvedVersion: string | null;
  name: string;
  x: number;
  y: number;
  width: number;
  height: number;
  inputChannelName: string | null;
  outputChannelNames: string[];
  hasUpgradeWarning: boolean;
  renderStatus: NodeModel['renderStatus'];
  failureMessage: string | null;
  inputProperties: NodeModel['inputProperties'];
  outputChannelProperties: NodeModel['outputChannelProperties'];
  designTimeInputOverrides: NodeModel['designTimeInputOverrides'];
}

interface GraphConnectionSnapshot {
  id: string;
  fromId: number;
  toId: number;
  anchor: number;
  side: ConnectionModel['side'];
  pos: number;
  propertyMappings: ConnectionPropertyMappingModel[];
}

interface GraphSnapshot {
  nodes: GraphNodeSnapshot[];
  connections: GraphConnectionSnapshot[];
  selectedNodeIds: number[];
  selectedConnectionIds: string[];
}

interface ClipboardSnapshot {
  nodes: GraphNodeSnapshot[];
  connections: GraphConnectionSnapshot[];
}

export interface ModifierState {
  shift: boolean;
  ctrlOrMeta: boolean;
  space: boolean;
}

export interface NodePositionUpdate {
  nodeId: number;
  coordinates: Point;
}

@Injectable({ providedIn: 'root' })
export class BlueprintEditorStore {
  private static readonly historyLimit = 100;
  private static readonly pasteOffset = 40;
  private static readonly gridSize = 20;

  private readonly _nodes = signal<NodeModel[]>([]);
  private readonly _connections = signal<ConnectionModel[]>([]);
  private readonly _selectedNodeIds = signal<number[]>([]);
  private readonly _selectedConnectionIds = signal<string[]>([]);
  private readonly _clipboard = signal<ClipboardSnapshot | null>(null);
  private readonly _history = signal<GraphSnapshot[]>([]);
  private readonly _future = signal<GraphSnapshot[]>([]);
  private readonly _canvasFocused = signal(false);
  private readonly _modifiers = signal<ModifierState>({ shift: false, ctrlOrMeta: false, space: false });

  private nextNodeId = 1;
  private pasteCount = 0;

  readonly nodes = this._nodes.asReadonly();
  readonly connections = this._connections.asReadonly();
  readonly selectedNodeIds = this._selectedNodeIds.asReadonly();
  readonly selectedConnectionIds = this._selectedConnectionIds.asReadonly();
  readonly canvasFocused = this._canvasFocused.asReadonly();
  readonly modifiers = this._modifiers.asReadonly();

  readonly selectedNodeIdSet = computed(() => new Set(this._selectedNodeIds()));
  readonly selectedConnectionIdSet = computed(() => new Set(this._selectedConnectionIds()));
  readonly hasSelection = computed(() =>
    this._selectedNodeIds().length > 0 || this._selectedConnectionIds().length > 0,
  );
  readonly canCopy = computed(() => this._selectedNodeIds().length > 0);
  readonly canPaste = computed(() => this._clipboard() !== null);
  readonly canDelete = computed(() => this.hasSelection());
  readonly canUndo = computed(() => this._history().length > 0);
  readonly canRedo = computed(() => this._future().length > 0);

  setCanvasFocused(value: boolean): void {
    this._canvasFocused.set(value);
  }

  setModifierState(state: ModifierState): void {
    this._modifiers.set(state);
  }

  loadGraph(nodes: NodeModel[], connections: ConnectionModel[]): void {
    this._nodes.set(nodes);
    this._connections.set(this.rebindConnections(connections, nodes));
    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([]);
    this._history.set([]);
    this._future.set([]);
    this.pasteCount = 0;
    this.nextNodeId = this.computeNextNodeId(nodes);
  }

  clearGraph(): void {
    if (this._nodes().length === 0 && this._connections().length === 0) {
      this.clearSelection();
      return;
    }

    this.captureHistory();
    this._nodes.set([]);
    this._connections.set([]);
    this.clearSelection();
    this.nextNodeId = 1;
  }

  createNode(taskDefinition: ITaskDefinition, coordinates: Point): NodeModel {
    const version = taskDefinition.versions[0];
    if (!version) {
      throw new Error(`Task definition ${taskDefinition.name} has no versions.`);
    }

    this.captureHistory();

    const [allowedMajor, allowedMinor] = this.getVersionLane(version.version);
    const node = new NodeModel(
      this.allocateNodeId(),
      taskDefinition.id,
      allowedMajor,
      allowedMinor,
      version.id,
      version.version,
      taskDefinition.name,
      this.snap(coordinates.x),
      this.snap(coordinates.y),
      320,
      Math.max(220, 168 + Math.max(version.outputChannels.length, 1) * 34),
      version.inputChannel?.name ?? null,
      version.outputChannels.map((channel) => channel.name),
      false,
      null,
      null,
      version.inputChannel?.properties ?? {},
      Object.fromEntries(version.outputChannels.map((channel) => [channel.name, channel.properties])),
      {},
    );

    this._nodes.update((nodes) => [...nodes, node]);
    this._selectedNodeIds.set([node.id]);
    this._selectedConnectionIds.set([]);
    return node;
  }

  updateNodePositions(updates: NodePositionUpdate[]): void {
    if (updates.length === 0) {
      return;
    }

    const updatesById = new Map(updates.map((update) => [update.nodeId, update.coordinates] as const));
    const nodes = this._nodes().map((node) => {
      const nextCoordinates = updatesById.get(node.id);
      if (!nextCoordinates) {
        return node;
      }

      return new NodeModel(
        node.id,
        node.taskDefinitionId,
        node.allowedMajor,
        node.allowedMinor,
        node.resolvedVersionId,
        node.resolvedVersion,
        node.name,
        this.snap(nextCoordinates.x),
        this.snap(nextCoordinates.y),
        node.width,
        node.height,
        node.inputChannelName,
        node.outputChannelNames,
        node.hasUpgradeWarning,
        node.renderStatus,
        node.failureMessage,
        node.inputProperties,
        node.outputChannelProperties,
        node.designTimeInputOverrides,
      );
    });

    this._nodes.set(nodes);
    this._connections.set(this.rebindConnections(this._connections(), nodes));
  }

  createConnection(
    sourceNodeId: number,
    destinationNodeId: number,
    anchor: number,
    side: ConnectionModel['side'],
    position: number,
  ): ConnectionModel | null {
    const from = this.getNodeById(sourceNodeId);
    const to = this.getNodeById(destinationNodeId);
    if (!from || !to) {
      return null;
    }

    const duplicate = this._connections().some((connection) =>
      connection.from.id === sourceNodeId &&
      connection.to.id === destinationNodeId &&
      connection.anchor === anchor &&
      connection.side === side &&
      connection.pos === this.snap(position),
    );
    if (duplicate) {
      return null;
    }

    this.captureHistory();
    const defaultMappings = this.getDefaultConnectionMappings(from, to, anchor);
    const connection = new ConnectionModel(from, to, anchor, side, this.snap(position), crypto.randomUUID(), defaultMappings);
    this._connections.update((connections) => [...connections, connection]);
    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([connection.id]);
    return connection;
  }

  updateNodeDesignTimeOverrides(nodeId: number, overrides: Record<string, unknown>): void {
    const node = this.getNodeById(nodeId);
    if (!node) {
      return;
    }

    this.captureHistory();
    this._nodes.update((nodes) => nodes.map((candidate) =>
      candidate.id !== nodeId
        ? candidate
        : new NodeModel(
          candidate.id,
          candidate.taskDefinitionId,
          candidate.allowedMajor,
          candidate.allowedMinor,
          candidate.resolvedVersionId,
          candidate.resolvedVersion,
          candidate.name,
          candidate.x,
          candidate.y,
          candidate.width,
          candidate.height,
          candidate.inputChannelName,
          candidate.outputChannelNames,
          candidate.hasUpgradeWarning,
          candidate.renderStatus,
          candidate.failureMessage,
          candidate.inputProperties,
          candidate.outputChannelProperties,
          { ...overrides },
        ),
    ));
    this._connections.set(this.rebindConnections(this._connections(), this._nodes()));
  }

  updateConnectionPropertyMappings(connectionId: string, propertyMappings: ConnectionPropertyMappingModel[]): void {
    const connection = this._connections().find((candidate) => candidate.id === connectionId);
    if (!connection) {
      return;
    }

    this.captureHistory();
    this._connections.update((connections) => connections.map((candidate) =>
      candidate.id !== connectionId
        ? candidate
        : new ConnectionModel(
          candidate.from,
          candidate.to,
          candidate.anchor,
          candidate.side,
          candidate.pos,
          candidate.id,
          propertyMappings.map((mapping) => ({ ...mapping })),
        ),
    ));
  }

  selectOnlyNode(nodeId: number | null): void {
    if (nodeId === null) {
      this.clearSelection();
      return;
    }

    this._selectedNodeIds.set([nodeId]);
    this._selectedConnectionIds.set([]);
  }

  toggleNodeSelection(nodeId: number): void {
    const nextNodeIds = new Set(this._selectedNodeIds());
    if (nextNodeIds.has(nodeId)) {
      nextNodeIds.delete(nodeId);
    } else {
      nextNodeIds.add(nodeId);
    }

    const selectedNodeIds = [...nextNodeIds];
    this._selectedNodeIds.set(selectedNodeIds);
    this._selectedConnectionIds.set(this.collectInternalConnectionIds(new Set(selectedNodeIds)));
  }

  selectOnlyConnection(connectionId: string | null): void {
    if (connectionId === null) {
      this.clearSelection();
      return;
    }

    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([connectionId]);
  }

  toggleConnectionSelection(connectionId: string): void {
    const nextConnectionIds = new Set(this._selectedConnectionIds());
    if (nextConnectionIds.has(connectionId)) {
      nextConnectionIds.delete(connectionId);
    } else {
      nextConnectionIds.add(connectionId);
    }

    this._selectedConnectionIds.set([...nextConnectionIds]);
  }

  selectNodesWithinRect(
    left: number,
    top: number,
    right: number,
    bottom: number,
    additive: boolean,
  ): void {
    const enclosedNodeIds = this._nodes()
      .filter((node) => node.isFullyWithin(left, top, right, bottom))
      .map((node) => node.id);

    const nextNodeIds = additive
      ? [...new Set([...this._selectedNodeIds(), ...enclosedNodeIds])]
      : enclosedNodeIds;

    const nodeIdSet = new Set(nextNodeIds);
    this._selectedNodeIds.set(nextNodeIds);
    this._selectedConnectionIds.set(this.collectInternalConnectionIds(nodeIdSet));
  }

  clearSelection(): void {
    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([]);
  }

  deleteSelection(): void {
    if (!this.hasSelection()) {
      return;
    }

    this.captureHistory();

    const selectedNodeIds = new Set(this._selectedNodeIds());
    const selectedConnectionIds = new Set(this._selectedConnectionIds());

    const remainingNodes = this._nodes().filter((node) => !selectedNodeIds.has(node.id));
    const remainingConnections = this._connections().filter((connection) =>
      !selectedConnectionIds.has(connection.id) &&
      !selectedNodeIds.has(connection.from.id) &&
      !selectedNodeIds.has(connection.to.id),
    );

    this._nodes.set(remainingNodes);
    this._connections.set(this.rebindConnections(remainingConnections, remainingNodes));
    this.clearSelection();
  }

  copySelection(): void {
    const selectedNodeIds = new Set(this._selectedNodeIds());
    if (selectedNodeIds.size === 0) {
      return;
    }

    const nodes = this._nodes()
      .filter((node) => selectedNodeIds.has(node.id))
      .map((node) => this.snapshotNode(node));
    const connections = this._connections()
      .filter((connection) =>
        selectedNodeIds.has(connection.from.id) && selectedNodeIds.has(connection.to.id),
      )
      .map((connection) => this.snapshotConnection(connection));

    this._clipboard.set({ nodes, connections });
    this.pasteCount = 0;
  }

  pasteClipboard(): void {
    const clipboard = this._clipboard();
    if (!clipboard || clipboard.nodes.length === 0) {
      return;
    }

    this.captureHistory();

    const offset = BlueprintEditorStore.pasteOffset * (this.pasteCount + 1);
    const nextNodes: NodeModel[] = [];
    const nodeIdMap = new Map<number, number>();

    for (const nodeSnapshot of clipboard.nodes) {
      const nextNodeId = this.allocateNodeId();
      nodeIdMap.set(nodeSnapshot.id, nextNodeId);
      nextNodes.push(new NodeModel(
        nextNodeId,
        nodeSnapshot.taskDefinitionId,
        nodeSnapshot.allowedMajor,
        nodeSnapshot.allowedMinor,
        nodeSnapshot.resolvedVersionId,
        nodeSnapshot.resolvedVersion,
        nodeSnapshot.name,
        this.snap(nodeSnapshot.x + offset),
        this.snap(nodeSnapshot.y + offset),
        nodeSnapshot.width,
        nodeSnapshot.height,
        nodeSnapshot.inputChannelName,
        nodeSnapshot.outputChannelNames,
        nodeSnapshot.hasUpgradeWarning,
        nodeSnapshot.renderStatus,
        nodeSnapshot.failureMessage,
        nodeSnapshot.inputProperties,
        nodeSnapshot.outputChannelProperties,
        nodeSnapshot.designTimeInputOverrides,
      ));
    }

    const nodes = [...this._nodes(), ...nextNodes];
    const nodeMap = new Map(nodes.map((node) => [node.id, node] as const));
    const nextConnections = clipboard.connections
      .map((connectionSnapshot) => {
        const fromId = nodeIdMap.get(connectionSnapshot.fromId);
        const toId = nodeIdMap.get(connectionSnapshot.toId);
        if (!fromId || !toId) {
          return null;
        }

        return new ConnectionModel(
          nodeMap.get(fromId)!,
          nodeMap.get(toId)!,
          connectionSnapshot.anchor,
          connectionSnapshot.side,
          connectionSnapshot.pos,
          crypto.randomUUID(),
          connectionSnapshot.propertyMappings,
        );
      })
      .filter((connection): connection is ConnectionModel => connection !== null);

    this._nodes.set(nodes);
    this._connections.set([...this._connections(), ...nextConnections]);
    this._selectedNodeIds.set(nextNodes.map((node) => node.id));
    this._selectedConnectionIds.set(nextConnections.map((connection) => connection.id));
    this.pasteCount += 1;
  }

  undo(): void {
    const history = this._history();
    const previous = history[history.length - 1];
    if (!previous) {
      return;
    }

    this._future.update((future) => [...future, this.snapshotGraph()]);
    this._history.set(history.slice(0, -1));
    this.restoreSnapshot(previous);
  }

  redo(): void {
    const future = this._future();
    const next = future[future.length - 1];
    if (!next) {
      return;
    }

    this._history.update((history) => [...history, this.snapshotGraph()]);
    this._future.set(future.slice(0, -1));
    this.restoreSnapshot(next);
  }

  captureHistory(): void {
    const snapshot = this.snapshotGraph();
    this._history.update((history) => {
      const nextHistory = [...history, snapshot];
      if (nextHistory.length <= BlueprintEditorStore.historyLimit) {
        return nextHistory;
      }

      return nextHistory.slice(nextHistory.length - BlueprintEditorStore.historyLimit);
    });
    this._future.set([]);
  }

  private restoreSnapshot(snapshot: GraphSnapshot): void {
    const nodes = snapshot.nodes.map((node) => this.instantiateNode(node));
    const nodeMap = new Map(nodes.map((node) => [node.id, node] as const));
    const connections = snapshot.connections
      .map((connection) => {
        const from = nodeMap.get(connection.fromId);
        const to = nodeMap.get(connection.toId);
        if (!from || !to) {
          return null;
        }

        return new ConnectionModel(
          from,
          to,
          connection.anchor,
          connection.side,
          connection.pos,
          connection.id,
          connection.propertyMappings,
        );
      })
      .filter((connection): connection is ConnectionModel => connection !== null);

    this._nodes.set(nodes);
    this._connections.set(connections);
    this._selectedNodeIds.set(snapshot.selectedNodeIds.filter((id) => nodeMap.has(id)));
    this._selectedConnectionIds.set(snapshot.selectedConnectionIds.filter((id) =>
      connections.some((connection) => connection.id === id),
    ));
    this.nextNodeId = this.computeNextNodeId(nodes);
  }

  private snapshotGraph(): GraphSnapshot {
    return {
      nodes: this._nodes().map((node) => this.snapshotNode(node)),
      connections: this._connections().map((connection) => this.snapshotConnection(connection)),
      selectedNodeIds: [...this._selectedNodeIds()],
      selectedConnectionIds: [...this._selectedConnectionIds()],
    };
  }

  private snapshotNode(node: NodeModel): GraphNodeSnapshot {
    return {
      id: node.id,
      taskDefinitionId: node.taskDefinitionId,
      allowedMajor: node.allowedMajor,
      allowedMinor: node.allowedMinor,
      resolvedVersionId: node.resolvedVersionId,
      resolvedVersion: node.resolvedVersion,
      name: node.name,
      x: node.x,
      y: node.y,
      width: node.width,
      height: node.height,
      inputChannelName: node.inputChannelName,
      outputChannelNames: [...node.outputChannelNames],
      hasUpgradeWarning: node.hasUpgradeWarning,
      renderStatus: node.renderStatus,
      failureMessage: node.failureMessage,
      inputProperties: { ...node.inputProperties },
      outputChannelProperties: Object.fromEntries(
        Object.entries(node.outputChannelProperties).map(([key, value]) => [key, { ...value }]),
      ),
      designTimeInputOverrides: { ...node.designTimeInputOverrides },
    };
  }

  private snapshotConnection(connection: ConnectionModel): GraphConnectionSnapshot {
    return {
      id: connection.id,
      fromId: connection.from.id,
      toId: connection.to.id,
      anchor: connection.anchor,
      side: connection.side,
      pos: connection.pos,
      propertyMappings: connection.propertyMappings.map((mapping) => ({ ...mapping })),
    };
  }

  private instantiateNode(node: GraphNodeSnapshot): NodeModel {
    return new NodeModel(
      node.id,
      node.taskDefinitionId,
      node.allowedMajor,
      node.allowedMinor,
      node.resolvedVersionId,
      node.resolvedVersion,
      node.name,
      node.x,
      node.y,
      node.width,
      node.height,
      node.inputChannelName,
      node.outputChannelNames,
      node.hasUpgradeWarning,
      node.renderStatus,
      node.failureMessage,
      node.inputProperties,
      node.outputChannelProperties,
      node.designTimeInputOverrides,
    );
  }

  private computeNextNodeId(nodes: NodeModel[]): number {
    const maxId = nodes.reduce((max, node) => Math.max(max, node.id), 0);
    return maxId + 1;
  }

  private allocateNodeId(): number {
    const nodeId = this.nextNodeId;
    this.nextNodeId += 1;
    return nodeId;
  }

  private collectInternalConnectionIds(selectedNodeIds: Set<number>): string[] {
    return this._connections()
      .filter((connection) =>
        selectedNodeIds.has(connection.from.id) && selectedNodeIds.has(connection.to.id),
      )
      .map((connection) => connection.id);
  }

  private getVersionLane(version: string): [number, number] {
    const [major, minor] = version.split('.').map((segment) => Number(segment));
    return [Number.isFinite(major) ? major : 0, Number.isFinite(minor) ? minor : 0];
  }

  private getNodeById(nodeId: number): NodeModel | undefined {
    return this._nodes().find((node) => node.id === nodeId);
  }

  private rebindConnections(connections: ConnectionModel[], nodes: NodeModel[]): ConnectionModel[] {
    const nodeMap = new Map(nodes.map((node) => [node.id, node] as const));
    return connections
      .map((connection) => {
        const from = nodeMap.get(connection.from.id);
        const to = nodeMap.get(connection.to.id);
        if (!from || !to) {
          return null;
        }

        return new ConnectionModel(
          from,
          to,
          connection.anchor,
          connection.side,
          connection.pos,
          connection.id,
          connection.propertyMappings,
        );
      })
      .filter((connection): connection is ConnectionModel => connection !== null);
  }

  private getDefaultConnectionMappings(
    from: NodeModel,
    to: NodeModel,
    anchor: number,
  ): ConnectionPropertyMappingModel[] {
    const sourceProperties = from.outputPropertiesForAnchor(anchor);
    return Object.keys(sourceProperties)
      .filter((property) => property in to.inputProperties)
      .sort((left, right) => left.localeCompare(right))
      .map((property) => ({
        sourceProperty: property,
        targetProperty: property,
      }));
  }

  private snap(value: number): number {
    return Math.round(value / BlueprintEditorStore.gridSize) * BlueprintEditorStore.gridSize;
  }
}
