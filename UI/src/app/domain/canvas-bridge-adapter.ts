import { RenderedNode } from './canvas-render-model';
import { NodePortDefinition, TaskPropertyType } from './node-model';

export interface CanvasOutputPropertyDragEvent {
  readonly nodeId: number;
  readonly nodeInstanceId: string | null;
  readonly nodeName: string;
  readonly channelName: string | null;
  readonly propertyName: string;
  readonly propertyKind: TaskPropertyType;
  readonly clientX: number;
  readonly clientY: number;
  readonly cancelled?: boolean;
}

export interface CanvasInputPropertyHit {
  readonly nodeId: number;
  readonly nodeInstanceId: string | null;
  readonly nodeName: string;
  readonly propertyName: string;
  readonly propertyKind: TaskPropertyType;
  readonly clientX: number;
  readonly clientY: number;
}

export interface CanvasPropertyClientPoint {
  readonly clientX: number;
  readonly clientY: number;
}

export interface CanvasBridgeDragSource {
  readonly from: number;
  readonly anchor: number;
  readonly sourceProperty?: string | null;
}

export function createCanvasOutputPropertyDragEvent(
  sourceNode: RenderedNode | undefined,
  drag: CanvasBridgeDragSource,
  clientX: number,
  clientY: number,
  cancelled: boolean,
): CanvasOutputPropertyDragEvent | null {
  if (!sourceNode || !drag.sourceProperty) {
    return null;
  }

  const propertyKind = sourceNode.base.outputPropertiesForAnchor(drag.anchor)[drag.sourceProperty];
  if (!propertyKind) {
    return null;
  }

  return {
    nodeId: sourceNode.id,
    nodeInstanceId: sourceNode.base.instanceId,
    nodeName: sourceNode.name,
    channelName: sourceNode.base.outputChannelNameForAnchor(drag.anchor),
    propertyName: drag.sourceProperty,
    propertyKind,
    clientX,
    clientY,
    cancelled,
  };
}

export function createCanvasInputPropertyHit(
  targetNode: RenderedNode,
  targetPort: NodePortDefinition | null,
  clientX: number,
  clientY: number,
): CanvasInputPropertyHit | null {
  if (targetPort?.kind !== 'input-property' || !targetPort.propertyName) {
    return null;
  }

  const propertyKind = targetNode.base.inputProperties[targetPort.propertyName];
  if (!propertyKind) {
    return null;
  }

  return {
    nodeId: targetNode.id,
    nodeInstanceId: targetNode.base.instanceId,
    nodeName: targetNode.name,
    propertyName: targetPort.propertyName,
    propertyKind,
    clientX,
    clientY,
  };
}
