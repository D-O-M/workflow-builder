import { Injectable, computed, signal } from '@angular/core';
import { ConnectionModel, ConnectionPropertyMappingModel } from './connection-model';
import { NodeModel, Point, TaskPropertyBaseType, TaskPropertyType } from './node-model';
import { ITaskDefinition, ITaskPropertyDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';
import { normalizeTaskPropertyDefinitions, taskPropertyKindMap } from './task-property-form-utils';
import { getLatestTaskVersion, getTaskVersionLane } from './task-version-utils';

interface GraphNodeSnapshot {
  id: number;
  nodeKind: NodeModel['nodeKind'];
  collectionIteratorItemKind: TaskPropertyBaseType | null;
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
  inputPropertyDefinitions: NodeModel['inputPropertyDefinitions'];
  outputChannelProperties: NodeModel['outputChannelProperties'];
  designTimeInputOverrides: NodeModel['designTimeInputOverrides'];
  inputChannelDisplayName: string | null;
  outputChannelDisplayNames: string[];
  outputChannelPropertyDefinitions: Record<string, ITaskPropertyDefinition[]>;
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

export interface NodeVersionMappingImpact {
  readonly connectionId: string;
  readonly sourceNodeName: string;
  readonly targetNodeName: string;
  readonly properties: readonly string[];
}

export interface NodeVersionRemovedConnectionImpact {
  readonly connectionId: string;
  readonly sourceNodeName: string;
  readonly targetNodeName: string;
  readonly reason: string;
}

export interface NodeVersionChangeImpact {
  readonly removedOverrideProperties: readonly string[];
  readonly removedIncomingMappings: readonly NodeVersionMappingImpact[];
  readonly removedOutgoingMappings: readonly NodeVersionMappingImpact[];
  readonly removedConnections: readonly NodeVersionRemovedConnectionImpact[];
  readonly hasDestructiveChanges: boolean;
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
  private readonly _graphRevision = signal(0);

  private nextNodeId = 1;
  private pasteCount = 0;

  readonly nodes = this._nodes.asReadonly();
  readonly connections = this._connections.asReadonly();
  readonly selectedNodeIds = this._selectedNodeIds.asReadonly();
  readonly selectedConnectionIds = this._selectedConnectionIds.asReadonly();
  readonly canvasFocused = this._canvasFocused.asReadonly();
  readonly modifiers = this._modifiers.asReadonly();
  readonly graphRevision = this._graphRevision.asReadonly();

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
    this.retuneCollectionIteratorNodes();
    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([]);
    this._history.set([]);
    this._future.set([]);
    this.pasteCount = 0;
    this.nextNodeId = this.computeNextNodeId(nodes);
    this.bumpGraphRevision();
  }

  clearGraph(): void {
    if (this._nodes().length === 0 && this._connections().length === 0) {
      this.clearSelection();
      this.nextNodeId = 1;
      this.bumpGraphRevision();
      return;
    }

    this.captureHistory();
    this._nodes.set([]);
    this._connections.set([]);
    this.clearSelection();
    this.nextNodeId = 1;
    this.bumpGraphRevision();
  }

  createNode(taskDefinition: ITaskDefinition, coordinates: Point): NodeModel {
    const version = getLatestTaskVersion(taskDefinition.versions);
    if (!version) {
      throw new Error(`Task definition ${taskDefinition.name} has no versions.`);
    }

    this.captureHistory();

    const node = this.createNodeModelFromVersion(
      this.allocateNodeId(),
      taskDefinition.id,
      this.taskDefinitionDisplayName(taskDefinition),
      this.snap(coordinates.x),
      this.snap(coordinates.y),
      320,
      null,
      null,
      version,
      {},
    );

    this._nodes.update((nodes) => [...nodes, node]);
    this._selectedNodeIds.set([node.id]);
    this._selectedConnectionIds.set([]);
    return node;
  }

  createCollectionIteratorNode(coordinates: Point): NodeModel {
    this.captureHistory();
    const node = this.createCollectionIteratorNodeModel(
      this.allocateNodeId(),
      this.snap(coordinates.x),
      this.snap(coordinates.y),
      null,
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
        this.clonePropertyDefinitions(node.inputPropertyDefinitions),
        node.inputChannelDisplayName,
        [...node.outputChannelDisplayNames],
        this.cloneOutputChannelPropertyDefinitions(node.outputChannelPropertyDefinitions),
        node.expanded,
        node.nodeKind,
        node.collectionIteratorItemKind,
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
    if (!from || !to || sourceNodeId === destinationNodeId) {
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
    this.retuneCollectionIteratorNodes();
    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([connection.id]);
    return this._connections().find((candidate) => candidate.id === connection.id) ?? connection;
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
            this.clonePropertyDefinitions(candidate.inputPropertyDefinitions),
            candidate.inputChannelDisplayName,
            [...candidate.outputChannelDisplayNames],
            this.cloneOutputChannelPropertyDefinitions(candidate.outputChannelPropertyDefinitions),
            candidate.expanded,
            candidate.nodeKind,
            candidate.collectionIteratorItemKind,
          ),
    ));
    this._connections.set(this.rebindConnections(this._connections(), this._nodes()));
  }

  analyzeNodeVersionChange(nodeId: number, version: ITaskVersion): NodeVersionChangeImpact {
    const node = this.getNodeById(nodeId);
    if (!node) {
      return this.emptyNodeVersionChangeImpact();
    }

    const nextNode = this.createNodeModelFromVersion(
      node.id,
      node.taskDefinitionId,
      node.name,
      node.x,
      node.y,
      node.width,
      node.renderStatus,
      node.failureMessage,
      version,
      this.filterCompatibleOverrides(node, version),
    );
    const removedOverrideProperties = Object.keys(node.designTimeInputOverrides)
      .filter((propertyName) => !Object.prototype.hasOwnProperty.call(nextNode.designTimeInputOverrides, propertyName))
      .sort((left, right) => left.localeCompare(right));
    const removedIncomingMappings: NodeVersionMappingImpact[] = [];
    const removedOutgoingMappings: NodeVersionMappingImpact[] = [];
    const removedConnections: NodeVersionRemovedConnectionImpact[] = [];

    for (const connection of this._connections()) {
      if (connection.to.id === node.id) {
        if (!nextNode.inputChannelName) {
          removedConnections.push({
            connectionId: connection.id,
            sourceNodeName: connection.from.name,
            targetNodeName: connection.to.name,
            reason: 'The selected version has no input channel.',
          });
          continue;
        }

        const removedProperties = this.removedIncomingMappingProperties(connection, nextNode);
        if (removedProperties.length > 0) {
          removedIncomingMappings.push({
            connectionId: connection.id,
            sourceNodeName: connection.from.name,
            targetNodeName: connection.to.name,
            properties: removedProperties,
          });
        }
      }

      if (connection.from.id === node.id) {
        const oldChannelName = node.outputChannelNameForAnchor(connection.anchor);
        const nextAnchor = oldChannelName ? this.outputAnchorIndexByChannel(nextNode, oldChannelName) : null;
        if (nextAnchor === null) {
          removedConnections.push({
            connectionId: connection.id,
            sourceNodeName: connection.from.name,
            targetNodeName: connection.to.name,
            reason: oldChannelName
              ? `The selected version no longer has the ${oldChannelName} output channel.`
              : 'The selected version no longer has the connected output channel.',
          });
          continue;
        }

        const removedProperties = this.removedOutgoingMappingProperties(connection, nextNode, nextAnchor);
        if (removedProperties.length > 0) {
          removedOutgoingMappings.push({
            connectionId: connection.id,
            sourceNodeName: connection.from.name,
            targetNodeName: connection.to.name,
            properties: removedProperties,
          });
        }
      }
    }

    return {
      removedOverrideProperties,
      removedIncomingMappings,
      removedOutgoingMappings,
      removedConnections,
      hasDestructiveChanges:
        removedOverrideProperties.length > 0 ||
        removedIncomingMappings.length > 0 ||
        removedOutgoingMappings.length > 0 ||
        removedConnections.length > 0,
    };
  }

  updateNodeTaskVersion(nodeId: number, version: ITaskVersion): void {
    const node = this.getNodeById(nodeId);
    if (!node || node.resolvedVersionId === version.id) {
      return;
    }

    this.captureHistory();
    const nextNode = this.createNodeModelFromVersion(
      node.id,
      node.taskDefinitionId,
      node.name,
      node.x,
      node.y,
      node.width,
      node.renderStatus,
      node.failureMessage,
      version,
      this.filterCompatibleOverrides(node, version),
    );
    const nextNodes = this._nodes().map((candidate) => candidate.id === nodeId ? nextNode : candidate);
    const nodeMap = new Map(nextNodes.map((candidate) => [candidate.id, candidate] as const));
    const nextConnections = this._connections()
      .map((connection) => this.reconcileConnectionForNodeVersion(connection, node, nextNode, nodeMap))
      .filter((connection): connection is ConnectionModel => connection !== null);

    this._nodes.set(nextNodes);
    this._connections.set(nextConnections);
    this._selectedConnectionIds.set(this._selectedConnectionIds().filter((connectionId) =>
      nextConnections.some((connection) => connection.id === connectionId),
    ));
    this.retuneCollectionIteratorNodes();
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
    this.retuneCollectionIteratorNodes();
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
    this.retuneCollectionIteratorNodes();
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
        this.clonePropertyDefinitions(nodeSnapshot.inputPropertyDefinitions),
        nodeSnapshot.inputChannelDisplayName,
        [...nodeSnapshot.outputChannelDisplayNames],
        this.cloneOutputChannelPropertyDefinitions(nodeSnapshot.outputChannelPropertyDefinitions),
        false,
        nodeSnapshot.nodeKind,
        nodeSnapshot.collectionIteratorItemKind,
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
      nodeKind: node.nodeKind,
      collectionIteratorItemKind: node.collectionIteratorItemKind,
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
      inputPropertyDefinitions: this.clonePropertyDefinitions(node.inputPropertyDefinitions),
      outputChannelProperties: Object.fromEntries(
        Object.entries(node.outputChannelProperties).map(([key, value]) => [key, { ...value }]),
      ),
      designTimeInputOverrides: { ...node.designTimeInputOverrides },
      inputChannelDisplayName: node.inputChannelDisplayName,
      outputChannelDisplayNames: [...node.outputChannelDisplayNames],
      outputChannelPropertyDefinitions: this.cloneOutputChannelPropertyDefinitions(node.outputChannelPropertyDefinitions),
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
      this.clonePropertyDefinitions(node.inputPropertyDefinitions),
      node.inputChannelDisplayName,
      [...node.outputChannelDisplayNames],
      this.cloneOutputChannelPropertyDefinitions(node.outputChannelPropertyDefinitions),
      false,
      node.nodeKind,
      node.collectionIteratorItemKind,
    );
  }

  private clonePropertyDefinitions(properties: ITaskPropertyDefinition[] | Record<string, NodeModel['inputProperties'][string]>): ITaskPropertyDefinition[] {
    return normalizeTaskPropertyDefinitions(properties);
  }

  private cloneOutputChannelPropertyDefinitions(
    outputChannels: { name: string; properties: ITaskPropertyDefinition[] }[] | Record<string, ITaskPropertyDefinition[]>,
  ): Record<string, ITaskPropertyDefinition[]> {
    if (Array.isArray(outputChannels)) {
      return Object.fromEntries(outputChannels.map((channel) => [
        channel.name,
        this.clonePropertyDefinitions(channel.properties),
      ]));
    }

    return Object.fromEntries(
      Object.entries(outputChannels).map(([channelName, properties]) => [
        channelName,
        this.clonePropertyDefinitions(properties),
      ]),
    );
  }

  private createNodeModelFromVersion(
    nodeId: number,
    taskDefinitionId: number,
    taskName: string,
    x: number,
    y: number,
    width: number,
    renderStatus: NodeModel['renderStatus'],
    failureMessage: string | null,
    version: ITaskVersion,
    designTimeInputOverrides: Record<string, unknown>,
  ): NodeModel {
    const [allowedMajor, allowedMinor] = getTaskVersionLane(version.version);
    const inputPropertyDefinitions = this.clonePropertyDefinitions(version.inputChannel?.properties ?? []);
    const outputChannelPropertyDefinitions = this.cloneOutputChannelPropertyDefinitions(version.outputChannels);

    return new NodeModel(
      nodeId,
      taskDefinitionId,
      allowedMajor,
      allowedMinor,
      version.id,
      version.version,
      taskName,
      x,
      y,
      width,
      Math.max(220, 168 + Math.max(version.outputChannels.length, 1) * 34),
      version.inputChannel?.name ?? null,
      version.outputChannels.map((channel) => channel.name),
      false,
      renderStatus,
      failureMessage,
      taskPropertyKindMap(inputPropertyDefinitions),
      Object.fromEntries(version.outputChannels.map((channel) => [
        channel.name,
        taskPropertyKindMap(channel.properties),
      ])),
      { ...designTimeInputOverrides },
      inputPropertyDefinitions,
      version.inputChannel?.displayName ?? null,
      version.outputChannels.map((channel) => channel.displayName?.trim() || channel.name),
      outputChannelPropertyDefinitions,
      false,
      'PluginTask',
      null,
    );
  }

  private createCollectionIteratorNodeModel(
    nodeId: number,
    x: number,
    y: number,
    itemKind: TaskPropertyBaseType | null,
  ): NodeModel {
    const concreteItemKind = itemKind ?? 'String';
    const inputProperties: Record<string, TaskPropertyType> = {
      items: `${concreteItemKind}[]`,
    };
    const outputChannelProperties: Record<string, Record<string, TaskPropertyType>> = {
      Item: {
        item: concreteItemKind,
      },
    };
    const inputPropertyDefinitions: ITaskPropertyDefinition[] = [{
      name: 'items',
      kind: `${concreteItemKind}[]`,
      required: true,
      enumValues: [],
    }];
    const outputChannelPropertyDefinitions: Record<string, ITaskPropertyDefinition[]> = {
      Item: [{
        name: 'item',
        kind: concreteItemKind,
        required: true,
        enumValues: [],
      }],
    };

    return new NodeModel(
      nodeId,
      0,
      0,
      0,
      0,
      'Native',
      'Collection Iterator',
      x,
      y,
      320,
      220,
      'Collection',
      ['Item'],
      false,
      null,
      null,
      inputProperties,
      outputChannelProperties,
      {},
      inputPropertyDefinitions,
      'Collection',
      ['Item'],
      outputChannelPropertyDefinitions,
      false,
      'CollectionIterator',
      itemKind,
    );
  }

  private taskDefinitionDisplayName(taskDefinition: ITaskDefinition): string {
    const displayName = taskDefinition.displayName?.trim();
    return displayName && displayName.length > 0
      ? displayName
      : taskDefinition.name;
  }

  private filterCompatibleOverrides(node: NodeModel, version: ITaskVersion): Record<string, unknown> {
    const nextProperties = new Map(
      this.clonePropertyDefinitions(version.inputChannel?.properties ?? []).map((property) =>
        [property.name, property.kind] as const,
      ),
    );
    const currentProperties = new Map(node.inputPropertyDefinitions.map((property) =>
      [property.name, property.kind] as const,
    ));
    const compatibleOverrides: Record<string, unknown> = {};

    for (const [propertyName, value] of Object.entries(node.designTimeInputOverrides)) {
      const nextKind = nextProperties.get(propertyName);
      if (!nextKind) {
        continue;
      }

      const currentKind = currentProperties.get(propertyName);
      if (currentKind && currentKind !== nextKind) {
        continue;
      }

      compatibleOverrides[propertyName] = value;
    }

    return compatibleOverrides;
  }

  private emptyNodeVersionChangeImpact(): NodeVersionChangeImpact {
    return {
      removedOverrideProperties: [],
      removedIncomingMappings: [],
      removedOutgoingMappings: [],
      removedConnections: [],
      hasDestructiveChanges: false,
    };
  }

  private removedIncomingMappingProperties(connection: ConnectionModel, nextTargetNode: NodeModel): string[] {
    const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
    return connection.propertyMappings
      .filter((mapping) =>
        !Object.prototype.hasOwnProperty.call(nextTargetNode.inputProperties, mapping.targetProperty) ||
        !Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty))
      .map((mapping) => mapping.targetProperty)
      .sort((left, right) => left.localeCompare(right));
  }

  private removedOutgoingMappingProperties(
    connection: ConnectionModel,
    nextSourceNode: NodeModel,
    nextAnchor: number,
  ): string[] {
    const sourceProperties = nextSourceNode.outputPropertiesForAnchor(nextAnchor);
    return connection.propertyMappings
      .filter((mapping) =>
        !Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty) ||
        !Object.prototype.hasOwnProperty.call(connection.to.inputProperties, mapping.targetProperty))
      .map((mapping) => mapping.sourceProperty)
      .sort((left, right) => left.localeCompare(right));
  }

  private reconcileConnectionForNodeVersion(
    connection: ConnectionModel,
    previousNode: NodeModel,
    nextNode: NodeModel,
    nodeMap: ReadonlyMap<number, NodeModel>,
  ): ConnectionModel | null {
    const from = connection.from.id === previousNode.id ? nextNode : nodeMap.get(connection.from.id);
    const to = connection.to.id === previousNode.id ? nextNode : nodeMap.get(connection.to.id);
    if (!from || !to || from.id === to.id) {
      return null;
    }

    if (connection.to.id === previousNode.id && !nextNode.inputChannelName) {
      return null;
    }

    let anchor = connection.anchor;
    if (connection.from.id === previousNode.id) {
      const previousChannelName = previousNode.outputChannelNameForAnchor(connection.anchor);
      const nextAnchor = previousChannelName ? this.outputAnchorIndexByChannel(nextNode, previousChannelName) : null;
      if (nextAnchor === null) {
        return null;
      }

      anchor = nextAnchor;
    }

    return new ConnectionModel(
      from,
      to,
      anchor,
      connection.side,
      connection.to.id === previousNode.id ? to.inputAnchorSnap()?.pos ?? connection.pos : connection.pos,
      connection.id,
      this.filterCompatibleConnectionMappings(connection.propertyMappings, from, to, anchor),
    );
  }

  private filterCompatibleConnectionMappings(
    mappings: readonly ConnectionPropertyMappingModel[],
    from: NodeModel,
    to: NodeModel,
    anchor: number,
  ): ConnectionPropertyMappingModel[] {
    const sourceProperties = from.outputPropertiesForAnchor(anchor);
    return mappings
      .filter((mapping) =>
        Object.prototype.hasOwnProperty.call(sourceProperties, mapping.sourceProperty) &&
        Object.prototype.hasOwnProperty.call(to.inputProperties, mapping.targetProperty) &&
        sourceProperties[mapping.sourceProperty] === to.inputProperties[mapping.targetProperty])
      .map((mapping) => ({ ...mapping }));
  }

  private outputAnchorIndexByChannel(node: NodeModel, channelName: string): number | null {
    const outputIndex = node.outputChannelNames.indexOf(channelName);
    if (outputIndex < 0) {
      return null;
    }

    return outputIndex + (node.inputChannelName ? 1 : 0);
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

  private getNodeById(nodeId: number): NodeModel | undefined {
    return this._nodes().find((node) => node.id === nodeId);
  }

  private rebindConnections(connections: ConnectionModel[], nodes: NodeModel[]): ConnectionModel[] {
    const nodeMap = new Map(nodes.map((node) => [node.id, node] as const));
    return connections
      .map((connection) => {
        const from = nodeMap.get(connection.from.id);
        const to = nodeMap.get(connection.to.id);
        if (!from || !to || from.id === to.id) {
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
    if (to.nodeKind === 'CollectionIterator') {
      const collectionProperty = Object.entries(sourceProperties)
        .filter(([, kind]) => this.isCollectionKind(kind))
        .sort(([left], [right]) => left.localeCompare(right))[0];
      return collectionProperty
        ? [{
          sourceProperty: collectionProperty[0],
          targetProperty: 'items',
        }]
        : [];
    }

    return Object.keys(sourceProperties)
      .filter((property) => property in to.inputProperties && sourceProperties[property] === to.inputProperties[property])
      .sort((left, right) => left.localeCompare(right))
      .map((property) => ({
        sourceProperty: property,
        targetProperty: property,
      }));
  }

  private retuneCollectionIteratorNodes(): void {
    const nodes = this._nodes();
    const tunedNodes = nodes.map((node) => {
      if (node.nodeKind !== 'CollectionIterator') {
        return node;
      }

      const incomingCollectionKind = this.collectionKindFromIncomingMappings(node.id);
      const itemKind = incomingCollectionKind
        ? this.toScalarKind(incomingCollectionKind)
        : null;
      if (itemKind === node.collectionIteratorItemKind) {
        return node;
      }

      return this.createCollectionIteratorNodeModel(
        node.id,
        node.x,
        node.y,
        itemKind,
      ).withExpanded(node.expanded);
    });

    if (tunedNodes.every((node, index) => node === nodes[index])) {
      return;
    }

    this._nodes.set(tunedNodes);
    this._connections.set(this.rebindConnections(this._connections(), tunedNodes));
  }

  private collectionKindFromIncomingMappings(nodeId: number): TaskPropertyType | null {
    const candidates: TaskPropertyType[] = [];
    for (const connection of this._connections()) {
      if (connection.to.id !== nodeId) {
        continue;
      }

      const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
      for (const mapping of connection.propertyMappings) {
        if (mapping.targetProperty !== 'items') {
          continue;
        }

        const sourceKind = sourceProperties[mapping.sourceProperty];
        if (sourceKind && this.isCollectionKind(sourceKind)) {
          candidates.push(sourceKind);
        }
      }
    }

    return [...new Set(candidates)].length === 1 ? candidates[0] : null;
  }

  private isCollectionKind(kind: TaskPropertyType): kind is `${TaskPropertyBaseType}[]` {
    return kind.endsWith('[]');
  }

  private toScalarKind(kind: TaskPropertyType): TaskPropertyBaseType {
    return this.isCollectionKind(kind)
      ? kind.slice(0, -2) as TaskPropertyBaseType
      : kind;
  }

  private snap(value: number): number {
    return Math.round(value / BlueprintEditorStore.gridSize) * BlueprintEditorStore.gridSize;
  }

  private bumpGraphRevision(): void {
    this._graphRevision.update((revision) => revision + 1);
  }
}
