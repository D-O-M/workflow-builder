import { BlueprintCanvasEvent } from '../components/blueprint-canvas-contract';
import { BlueprintEditorStore } from './blueprint-editor.store';
import {
  buildConnectionMappingSelections,
  buildConnectionMappingsFromSelections,
  ConnectionModel,
} from './connection-model';

export function applyBlueprintCanvasStoreEvent(
  store: BlueprintEditorStore,
  event: BlueprintCanvasEvent,
): ConnectionModel | null | undefined {
  switch (event.kind) {
    case 'focusChanged':
      store.setCanvasFocused(event.focused);
      return undefined;
    case 'selectOnlyNode':
      store.selectOnlyNode(event.nodeId);
      return undefined;
    case 'toggleNodeSelection':
      store.toggleNodeSelection(event.nodeId);
      return undefined;
    case 'selectOnlyConnection':
      store.selectOnlyConnection(event.connectionId);
      return undefined;
    case 'toggleConnectionSelection':
      store.toggleConnectionSelection(event.connectionId);
      return undefined;
    case 'selectNodesWithinRect':
      store.selectNodesWithinRect({
        bounds: event.bounds,
        additive: event.additive,
      });
      return undefined;
    case 'clearSelection':
      store.clearSelection();
      return undefined;
    case 'captureHistory':
      store.captureHistory();
      return undefined;
    case 'updateNodePositions':
      store.updateNodePositions([...event.positions]);
      return undefined;
    case 'createConnection':
      return createConnectionFromCanvasEvent(store, event.request);
    case 'readonlyNodeActivated':
    case 'nodeSettingsRequested':
    case 'bridgeOutputPropertyDrag':
    case 'viewportChanged':
      return undefined;
    default:
      return exhaustiveEvent(event);
  }
}

function createConnectionFromCanvasEvent(
  store: BlueprintEditorStore,
  request: Extract<BlueprintCanvasEvent, { kind: 'createConnection' }>['request'],
): ConnectionModel | null {
  const bridgeInputProperty = request.upsertDataBridgeInputProperty;
  if (bridgeInputProperty &&
    !store.upsertDataBridgeInputProperty({
      bridgeNodeId: bridgeInputProperty.nodeId,
      property: {
        name: bridgeInputProperty.propertyName,
        kind: bridgeInputProperty.propertyKind,
      },
    })) {
    return null;
  }

  const connection = store.createConnection({
    source: {
      nodeId: request.from,
      anchor: request.anchor,
    },
    destination: {
      nodeId: request.to,
      side: request.side,
      position: request.position,
    },
  });
  if (!connection) {
    return null;
  }

  if (request.propertyMapping) {
    const nextSelections = buildConnectionMappingSelections(connection);
    nextSelections[request.propertyMapping.targetProperty] = request.propertyMapping.sourceProperty;
    store.updateConnectionPropertyMappings({
      connectionId: connection.id,
      propertyMappings: buildConnectionMappingsFromSelections(nextSelections),
    });
    return store.connections().find((candidate) => candidate.id === connection.id) ?? connection;
  }

  return connection;
}

function exhaustiveEvent(event: never): never {
  throw new Error(`Unhandled canvas event: ${JSON.stringify(event)}`);
}
