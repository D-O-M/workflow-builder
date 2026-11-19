import { Injectable, computed, signal } from '@angular/core';
import { ConnectionModel } from './connection-model';
import { markChangedConditionalFields } from './conditional-rule';
import { NodeModel } from './node-model';
import { ITaskDefinition, ITaskVersion } from '../interfaces/ITaskDefinition';
import { getLatestTaskVersion } from './task-version-utils';
import { CanvasModifierState } from '../components/blueprint-canvas-contract';
import {
  BlueprintCollectionIteratorCreateRequest,
  BlueprintConditionalBranchCreateRequest,
  BlueprintConditionalRuleUpdate,
  BlueprintConnectionCreateRequest,
  BlueprintConnectionMappingUpdate,
  BlueprintDataBridgeInputPropertyRequest,
  BlueprintGraphDraft,
  BlueprintMarqueeSelectionRequest,
  BlueprintNodeDesignTimeOverridesUpdate,
  BlueprintNodePositionUpdate,
  BlueprintNodeVersionChangeRequest,
  BlueprintTaskNodeCreateRequest,
} from './blueprint-editor-store-contract';
import {
  canCreateBlueprintConnection,
  collectionKindFromIncomingBlueprintMappings,
  createBlueprintConnectionModel,
  getDefaultBlueprintConnectionMappings,
  isDuplicateBlueprintConnection,
  outputAnchorIndexByBlueprintChannel,
  rebindBlueprintConnections,
  reconcileBlueprintConnectionForNodeVersion,
  removedIncomingBlueprintMappingProperties,
  removedOutgoingBlueprintMappingProperties,
  toBlueprintScalarKind,
} from './blueprint-editor-connection-policy';
import {
  cloneBlueprintPropertyDefinitions,
  createBlueprintCollectionIteratorNodeModel,
  createBlueprintConditionalBranchNodeModel,
  createBlueprintPluginTaskNodeModel,
  instantiateBlueprintNodeSnapshot,
  moveBlueprintNodeModel,
  updateBlueprintNodeDesignTimeOverridesModel,
  upsertBlueprintDataBridgeInputPropertyModel,
} from './blueprint-editor-node-factory';
import {
  BlueprintClipboardSnapshot,
  BlueprintGraphSnapshot,
  restoreBlueprintGraphSnapshot,
  snapshotBlueprintClipboard,
  snapshotBlueprintGraph,
} from './blueprint-editor-graph-snapshot-utility';
import {
  emptyBlueprintSelection,
  selectBlueprintNodesWithinRect,
  selectOnlyBlueprintConnection,
  selectOnlyBlueprintNode,
  toggleBlueprintConnectionSelection,
  toggleBlueprintNodeSelection,
} from './blueprint-editor-selection-utility';

export type NodePositionUpdate = BlueprintNodePositionUpdate;

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
  private readonly _clipboard = signal<BlueprintClipboardSnapshot | null>(null);
  private readonly _history = signal<BlueprintGraphSnapshot[]>([]);
  private readonly _future = signal<BlueprintGraphSnapshot[]>([]);
  private readonly _canvasFocused = signal(false);
  private readonly _modifiers = signal<CanvasModifierState>({ shift: false, ctrlOrMeta: false, space: false });
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

  setModifierState(state: CanvasModifierState): void {
    this._modifiers.set(state);
  }

  loadGraph(graph: BlueprintGraphDraft): void {
    const nodes = [...graph.nodes];
    this._nodes.set(nodes);
    this._connections.set(rebindBlueprintConnections(graph.connections, nodes));
    this.retuneNativeNodes();
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

  createNode(request: BlueprintTaskNodeCreateRequest): NodeModel {
    const version = getLatestTaskVersion(request.taskDefinition.versions);
    if (!version) {
      throw new Error(`Task definition ${request.taskDefinition.name} has no versions.`);
    }

    this.captureHistory();

    const node = createBlueprintPluginTaskNodeModel({
      nodeId: this.allocateNodeId(),
      taskDefinitionId: request.taskDefinition.id,
      taskName: this.taskDefinitionDisplayName(request.taskDefinition),
      coordinates: {
        x: this.snap(request.placement.coordinates.x),
        y: this.snap(request.placement.coordinates.y),
      },
      width: 320,
      renderStatus: null,
      failureMessage: null,
      version,
      designTimeInputOverrides: {},
    });

    this._nodes.update((nodes) => [...nodes, node]);
    this._selectedNodeIds.set([node.id]);
    this._selectedConnectionIds.set([]);
    return node;
  }

  createConditionalBranchNode(request: BlueprintConditionalBranchCreateRequest): NodeModel {
    this.captureHistory();
    const { x, y } = request.placement.coordinates;
    const node = createBlueprintConditionalBranchNodeModel({
      nodeId: this.allocateNodeId(), coordinates: { x: this.snap(x), y: this.snap(y) },
    });
    this._nodes.update(nodes => [...nodes, node]);
    this.selectOnlyNode(node.id);
    return node;
  }

  updateConditionalRule(request: BlueprintConditionalRuleUpdate): void {
    const node = this.getNodeById(request.nodeId);
    if (node?.nodeKind !== 'ConditionalBranch') return;
    this.captureHistory();
    const replacement = createBlueprintConditionalBranchNodeModel({
      nodeId: node.id, coordinates: node, fields: node.inputPropertyDefinitions, rule: request.rule,
      instanceId: node.instanceId,
    });
    this._nodes.update(nodes => nodes.map(current => current === node ? replacement : current));
    this._connections.set(rebindBlueprintConnections(this._connections(), this._nodes()));
  }

  createCollectionIteratorNode(request: BlueprintCollectionIteratorCreateRequest): NodeModel {
    this.captureHistory();
    const node = createBlueprintCollectionIteratorNodeModel({
      nodeId: this.allocateNodeId(),
      coordinates: {
        x: this.snap(request.placement.coordinates.x),
        y: this.snap(request.placement.coordinates.y),
      },
      itemKind: request.itemKind ?? null,
    });

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

      return moveBlueprintNodeModel(node, {
        x: this.snap(nextCoordinates.x),
        y: this.snap(nextCoordinates.y),
      });
    });

    this._nodes.set(nodes);
    this._connections.set(rebindBlueprintConnections(this._connections(), nodes));
  }

  createConnection(request: BlueprintConnectionCreateRequest): ConnectionModel | null {
    const from = this.getNodeById(request.source.nodeId);
    const to = this.getNodeById(request.destination.nodeId);
    if (!from || !to || request.source.nodeId === request.destination.nodeId) {
      return null;
    }

    if (to.nodeKind === 'ConditionalBranch' &&
        (from.isDataBridge || !from.outputChannelNameForAnchor(request.source.anchor) ||
          this._connections().some(connection => connection.to.id === to.id))) {
      return null;
    }

    if (!canCreateBlueprintConnection(from, to)) {
      return null;
    }

    const resolvedRequest = {
      source: {
        node: from,
        anchor: request.source.anchor,
      },
      destination: {
        node: to,
        side: request.destination.side,
        position: this.snap(request.destination.position),
      },
    };
    if (isDuplicateBlueprintConnection(this._connections(), resolvedRequest)) {
      return null;
    }

    this.captureHistory();
    const defaultMappings = getDefaultBlueprintConnectionMappings(from, to, request.source.anchor);
    const connection = createBlueprintConnectionModel({
      ...resolvedRequest,
      propertyMappings: defaultMappings,
    });
    this._connections.update((connections) => [...connections, connection]);
    this.retuneNativeNodes();
    this._selectedNodeIds.set([]);
    this._selectedConnectionIds.set([connection.id]);
    return this._connections().find((candidate) => candidate.id === connection.id) ?? connection;
  }

  upsertDataBridgeInputProperty(request: BlueprintDataBridgeInputPropertyRequest): boolean {
    const normalizedPropertyName = request.property.name.trim();
    if (!normalizedPropertyName) {
      return false;
    }

    const bridge = this.getNodeById(request.bridgeNodeId);
    if (!bridge?.isDataBridge || bridge.dataBridgeKind !== 'output' || !bridge.inputChannelName) {
      return false;
    }

    const existingKind = bridge.inputProperties[normalizedPropertyName];
    if (existingKind && existingKind !== request.property.kind) {
      return false;
    }

    if (existingKind === request.property.kind) {
      return true;
    }

    this.captureHistory();
    this._nodes.update((nodes) => nodes.map((node) => {
      if (node.id !== request.bridgeNodeId) {
        return node;
      }

      return upsertBlueprintDataBridgeInputPropertyModel(node, {
        name: normalizedPropertyName,
        kind: request.property.kind,
      });
    }));
    this._connections.set(rebindBlueprintConnections(this._connections(), this._nodes()));
    return true;
  }

  updateNodeDesignTimeOverrides(request: BlueprintNodeDesignTimeOverridesUpdate): void {
    const node = this.getNodeById(request.nodeId);
    if (!node) {
      return;
    }

    this.captureHistory();
    this._nodes.update((nodes) => nodes.map((candidate) =>
      candidate.id !== request.nodeId
        ? candidate
        : updateBlueprintNodeDesignTimeOverridesModel(candidate, request.overrides),
    ));
    this._connections.set(rebindBlueprintConnections(this._connections(), this._nodes()));
  }

  analyzeNodeVersionChange(request: BlueprintNodeVersionChangeRequest): NodeVersionChangeImpact {
    const node = this.getNodeById(request.nodeId);
    if (!node) {
      return this.emptyNodeVersionChangeImpact();
    }

    const nextNode = createBlueprintPluginTaskNodeModel({
      nodeId: node.id,
      taskDefinitionId: node.taskDefinitionId,
      taskName: node.name,
      coordinates: { x: node.x, y: node.y },
      width: node.width,
      renderStatus: node.renderStatus,
      failureMessage: node.failureMessage,
      version: request.version,
      designTimeInputOverrides: this.filterCompatibleOverrides(node, request.version),
    });
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

        const removedProperties = removedIncomingBlueprintMappingProperties(connection, nextNode);
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
        const nextAnchor = oldChannelName ? outputAnchorIndexByBlueprintChannel(nextNode, oldChannelName) : null;
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

        const removedProperties = removedOutgoingBlueprintMappingProperties(connection, nextNode, nextAnchor);
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

  updateNodeTaskVersion(request: BlueprintNodeVersionChangeRequest): void {
    const node = this.getNodeById(request.nodeId);
    if (!node || node.resolvedVersionId === request.version.id) {
      return;
    }

    this.captureHistory();
    const nextNode = createBlueprintPluginTaskNodeModel({
      nodeId: node.id,
      taskDefinitionId: node.taskDefinitionId,
      taskName: node.name,
      coordinates: { x: node.x, y: node.y },
      width: node.width,
      renderStatus: node.renderStatus,
      failureMessage: node.failureMessage,
      version: request.version,
      designTimeInputOverrides: this.filterCompatibleOverrides(node, request.version),
    });
    const nextNodes = this._nodes().map((candidate) => candidate.id === request.nodeId ? nextNode : candidate);
    const nodeMap = new Map(nextNodes.map((candidate) => [candidate.id, candidate] as const));
    const nextConnections = this._connections()
      .map((connection) => reconcileBlueprintConnectionForNodeVersion({
        connection,
        previousNode: node,
        nextNode,
        nodeMap,
      }))
      .filter((connection): connection is ConnectionModel => connection !== null);

    this._nodes.set(nextNodes);
    this._connections.set(nextConnections);
    this._selectedConnectionIds.set(this._selectedConnectionIds().filter((connectionId) =>
      nextConnections.some((connection) => connection.id === connectionId),
    ));
    this.retuneNativeNodes();
  }

  updateConnectionPropertyMappings(request: BlueprintConnectionMappingUpdate): void {
    const connection = this._connections().find((candidate) => candidate.id === request.connectionId);
    if (!connection || connection.to.nodeKind === 'ConditionalBranch') {
      return;
    }

    this.captureHistory();
    this._connections.update((connections) => connections.map((candidate) =>
      candidate.id !== request.connectionId
        ? candidate
        : createBlueprintConnectionModel({
            source: {
              node: candidate.from,
              anchor: candidate.anchor,
            },
            destination: {
              node: candidate.to,
              side: candidate.side,
              position: candidate.pos,
            },
            id: candidate.id,
            propertyMappings: request.propertyMappings,
          }),
    ));
    this.retuneNativeNodes();
  }

  selectOnlyNode(nodeId: number | null): void {
    this.applySelection(selectOnlyBlueprintNode(nodeId));
  }

  toggleNodeSelection(nodeId: number): void {
    this.applySelection(toggleBlueprintNodeSelection({
      selectedNodeIds: this._selectedNodeIds(),
      connections: this._connections(),
      nodeId,
    }));
  }

  selectOnlyConnection(connectionId: string | null): void {
    this.applySelection(selectOnlyBlueprintConnection(connectionId));
  }

  toggleConnectionSelection(connectionId: string): void {
    this.applySelection(toggleBlueprintConnectionSelection({
      selectedNodeIds: this._selectedNodeIds(),
      selectedConnectionIds: this._selectedConnectionIds(),
      connectionId,
    }));
  }

  selectNodesWithinRect(request: BlueprintMarqueeSelectionRequest): void {
    this.applySelection(selectBlueprintNodesWithinRect({
      ...request,
      nodes: this._nodes(),
      connections: this._connections(),
      selectedNodeIds: this._selectedNodeIds(),
    }));
  }

  clearSelection(): void {
    this.applySelection(emptyBlueprintSelection());
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
    this._connections.set(rebindBlueprintConnections(remainingConnections, remainingNodes));
    this.retuneNativeNodes();
    this.clearSelection();
  }

  copySelection(): void {
    const selectedNodeIds = new Set(this._selectedNodeIds());
    if (selectedNodeIds.size === 0) {
      return;
    }

    this._clipboard.set(snapshotBlueprintClipboard({
      nodes: this._nodes(),
      connections: this._connections(),
      selectedNodeIds,
    }));
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
      nextNodes.push(instantiateBlueprintNodeSnapshot({
        ...nodeSnapshot,
        id: nextNodeId,
        x: this.snap(nodeSnapshot.x + offset),
        y: this.snap(nodeSnapshot.y + offset),
        instanceId: null,
      }));
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

        return createBlueprintConnectionModel({
          source: {
            node: nodeMap.get(fromId)!,
            anchor: connectionSnapshot.anchor,
          },
          destination: {
            node: nodeMap.get(toId)!,
            side: connectionSnapshot.side,
            position: connectionSnapshot.pos,
          },
          propertyMappings: connectionSnapshot.propertyMappings,
        });
      })
      .filter((connection): connection is ConnectionModel => connection !== null);

    this._nodes.set(nodes);
    this._connections.set([...this._connections(), ...nextConnections]);
    this.retuneNativeNodes();
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

  private restoreSnapshot(snapshot: BlueprintGraphSnapshot): void {
    const restored = restoreBlueprintGraphSnapshot(snapshot);
    this._nodes.set([...restored.nodes]);
    this._connections.set([...restored.connections]);
    this._selectedNodeIds.set([...restored.selectedNodeIds]);
    this._selectedConnectionIds.set([...restored.selectedConnectionIds]);
    const nodes = [...restored.nodes];
    this.nextNodeId = this.computeNextNodeId(nodes);
  }

  private snapshotGraph(): BlueprintGraphSnapshot {
    return snapshotBlueprintGraph({
      nodes: this._nodes(),
      connections: this._connections(),
      selectedNodeIds: [...this._selectedNodeIds()],
      selectedConnectionIds: [...this._selectedConnectionIds()],
    });
  }

  private taskDefinitionDisplayName(taskDefinition: ITaskDefinition): string {
    const displayName = taskDefinition.displayName?.trim();
    return displayName && displayName.length > 0
      ? displayName
      : taskDefinition.name;
  }

  private filterCompatibleOverrides(node: NodeModel, version: ITaskVersion): Record<string, unknown> {
    const nextProperties = new Map(
      cloneBlueprintPropertyDefinitions(version.inputChannel?.properties ?? []).map((property) =>
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

  private computeNextNodeId(nodes: NodeModel[]): number {
    const maxId = nodes.reduce((max, node) => Math.max(max, node.id), 0);
    return maxId + 1;
  }

  private allocateNodeId(): number {
    const nodeId = this.nextNodeId;
    this.nextNodeId += 1;
    return nodeId;
  }

  private getNodeById(nodeId: number): NodeModel | undefined {
    return this._nodes().find((node) => node.id === nodeId);
  }

  private applySelection(selection: { readonly selectedNodeIds: readonly number[]; readonly selectedConnectionIds: readonly string[] }): void {
    this._selectedNodeIds.set([...selection.selectedNodeIds]);
    this._selectedConnectionIds.set([...selection.selectedConnectionIds]);
  }

  private retuneNativeNodes(): void {
    const connections = this._connections();
    const incoming = new Map<number, ConnectionModel[]>();
    for (const connection of connections) {
      const list = incoming.get(connection.to.id) ?? [];
      list.push(connection);
      incoming.set(connection.to.id, list);
    }

    // Resolve native chains from their upstream schemas, even when the canvas order differs.
    const tuned = new Map<number, NodeModel>();
    const visiting = new Set<number>();
    const tune = (node: NodeModel): NodeModel => {
      const cached = tuned.get(node.id);
      if (cached) return cached;
      if (visiting.has(node.id) || (node.nodeKind !== 'ConditionalBranch' && node.nodeKind !== 'CollectionIterator')) {
        return node;
      }
      visiting.add(node.id);
      const sources = (incoming.get(node.id) ?? []).map(connection => createBlueprintConnectionModel({
        source: { node: tune(connection.from), anchor: connection.anchor },
        destination: { node, side: connection.side, position: connection.pos },
        id: connection.id, propertyMappings: connection.propertyMappings,
      }));
      let next = node;
      if (node.nodeKind === 'ConditionalBranch') {
        const source = sources[0];
        const fields = source?.from.outputPropertyDefinitionsForAnchor(source.anchor) ?? [];
        if (JSON.stringify(node.inputPropertyDefinitions) !== JSON.stringify(fields)) {
          const rule = node.conditionalRule && markChangedConditionalFields(node.conditionalRule, node.inputPropertyDefinitions, fields);
          next = createBlueprintConditionalBranchNodeModel({
            nodeId: node.id, coordinates: node, fields, rule, instanceId: node.instanceId,
          });
        }
      } else {
        const kind = collectionKindFromIncomingBlueprintMappings(node.id, sources);
        const itemKind = kind ? toBlueprintScalarKind(kind) : null;
        const enumValues = itemKind === 'Enum' ? this.iteratorEnumValues(sources) : [];
        if (itemKind !== node.collectionIteratorItemKind ||
            JSON.stringify(enumValues) !== JSON.stringify(node.inputPropertyDefinitions[0]?.enumValues ?? [])) {
          next = createBlueprintCollectionIteratorNodeModel({
            nodeId: node.id, coordinates: node, itemKind, enumValues, instanceId: node.instanceId, expanded: node.expanded,
          });
        }
      }
      visiting.delete(node.id);
      tuned.set(node.id, next);
      return next;
    };

    const nodes = this._nodes();
    const tunedNodes = nodes.map(tune);
    if (tunedNodes.some((node, i) => node !== nodes[i])) {
      this._nodes.set(tunedNodes);
      this._connections.set(rebindBlueprintConnections(connections, tunedNodes));
    }
  }

  private iteratorEnumValues(sources: readonly ConnectionModel[]): string[] {
    const members = new Set<string>();
    for (const edge of sources) {
      const binding = edge.propertyMappings.find(mapping => mapping.targetProperty === 'items');
      const sourceName = edge.propertyMappings.length ? binding?.sourceProperty : 'items';
      const property = edge.from.outputPropertyDefinitionsForAnchor(edge.anchor)
        .find(field => field.name === sourceName && field.kind === 'Enum[]');
      for (const member of property?.enumValues ?? []) members.add(member);
    }
    return [...members].sort();
  }

  private snap(value: number): number {
    return Math.round(value / BlueprintEditorStore.gridSize) * BlueprintEditorStore.gridSize;
  }

  private bumpGraphRevision(): void {
    this._graphRevision.update((revision) => revision + 1);
  }
}
