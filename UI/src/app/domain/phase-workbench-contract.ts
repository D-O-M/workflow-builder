import {
  BlueprintCanvasBridgePort,
  BlueprintCanvasEvent,
  BlueprintCanvasGraph,
  BlueprintCanvasRuntime,
  BlueprintCanvasSelection,
  BlueprintCanvasViewportPort,
  CanvasOutputPropertyDragEvent,
} from '../components/blueprint-canvas-contract';
import { TaskPropertyType } from './node-model';
import {
  DataBridgeMappingDraft,
  DataBridgeSourceProperty,
  PhaseSeriesPhaseCanvas,
} from './phase-series-canvas';
import { PhaseSplitMode } from './phase-bridge-geometry';

export interface PhaseWorkbenchDomRect {
  readonly left: number;
  readonly right: number;
  readonly top: number;
  readonly bottom: number;
  readonly width: number;
  readonly height: number;
}

export interface PhaseCanvasPort {
  readonly bridge: BlueprintCanvasBridgePort;
  readonly viewport: BlueprintCanvasViewportPort;
}

export interface PhaseCanvasPair {
  readonly sourcePhase: PhaseSeriesPhaseCanvas | null;
  readonly destinationPhase: PhaseSeriesPhaseCanvas | null;
  readonly sourceCanvas: PhaseCanvasPort | undefined;
  readonly destinationCanvas: PhaseCanvasPort | undefined;
  readonly splitMode: PhaseSplitMode;
}

export interface PhaseCanvasContractSet {
  readonly sourceGraph: BlueprintCanvasGraph;
  readonly destinationGraph: BlueprintCanvasGraph;
  readonly sourceRuntime: BlueprintCanvasRuntime;
  readonly destinationRuntime: BlueprintCanvasRuntime;
  readonly selection: BlueprintCanvasSelection;
}

export interface PhaseBridgeDragState {
  readonly kind: 'source-output' | 'bridge-output';
  readonly clientX: number;
  readonly clientY: number;
  readonly label: string;
  readonly propertyKind: TaskPropertyType;
  readonly mappingId: string | null;
  readonly bridgePosition?: number | null;
  readonly sourceNodeId?: number | null;
  readonly sourceBlueprintNodeId?: string | null;
  readonly sourceNodeName?: string | null;
  readonly sourceChannelName?: string | null;
  readonly sourceProperty?: string | null;
}

export interface PhaseBridgeSelection {
  readonly mappingId: string;
  readonly side: 'source' | 'destination';
}

export interface PhaseBridgeCurve {
  readonly id: string;
  readonly mappingId: string;
  readonly side: PhaseBridgeSelection['side'];
  readonly path: string;
  readonly state: 'complete' | 'incomplete' | 'issue' | 'preview';
}

export type PhaseBridgeGuidanceTone = 'neutral' | 'info' | 'warning' | 'success';

export interface PhaseBridgeGuidance {
  readonly icon: string;
  readonly text: string;
  readonly tone: PhaseBridgeGuidanceTone;
}

export type PhaseBridgeSourceDropResult =
  | { readonly kind: 'none' }
  | { readonly kind: 'stageSource'; readonly source: DataBridgeSourceProperty; readonly bridgePosition: number };

export type PhaseBridgeDeleteEffect =
  | { readonly kind: 'removeMapping'; readonly mappingId: string }
  | { readonly kind: 'clearTarget'; readonly mappingId: string };

export interface PhaseBridgeOutputDragCallbacks {
  readonly complete: (mappingId: string, clientX: number, clientY: number, cancelled: boolean) => void;
}

export interface PhaseBridgePointerEventTarget {
  addEventListener(type: 'pointermove', listener: (event: PointerEvent) => void): void;
  addEventListener(type: 'pointerup' | 'pointercancel', listener: (event: PointerEvent) => void, options?: AddEventListenerOptions): void;
  removeEventListener(type: 'pointermove' | 'pointerup' | 'pointercancel', listener: (event: PointerEvent) => void): void;
}

export interface PhaseCanvasEventHandlers {
  readonly viewportChanged: () => void;
  readonly sourceDragStarted?: (event: CanvasOutputPropertyDragEvent) => void;
  readonly sourceDragMoved?: (event: CanvasOutputPropertyDragEvent) => void;
  readonly sourceDragEnded?: (event: CanvasOutputPropertyDragEvent) => void;
}

export interface PhaseCanvasPresenter {
  graph(phase: PhaseSeriesPhaseCanvas, revision: number): BlueprintCanvasGraph;
  sourceRuntime(viewportInsets: BlueprintCanvasRuntime['viewportInsets']): BlueprintCanvasRuntime;
  destinationRuntime(viewportInsets: BlueprintCanvasRuntime['viewportInsets']): BlueprintCanvasRuntime;
  selection(): BlueprintCanvasSelection;
  handleSourceEvent(event: BlueprintCanvasEvent, handlers: PhaseCanvasEventHandlers): void;
  handleDestinationEvent(event: BlueprintCanvasEvent, handlers: Pick<PhaseCanvasEventHandlers, 'viewportChanged'>): void;
  reset(canvas: PhaseCanvasPort | undefined): void;
  zoomIn(canvas: PhaseCanvasPort | undefined): void;
  zoomOut(canvas: PhaseCanvasPort | undefined): void;
}

export type PhaseSetupEvent =
  | { readonly kind: 'createSeries' }
  | { readonly kind: 'openSeries'; readonly definitionId: number }
  | { readonly kind: 'addPhase'; readonly optionValue: string }
  | { readonly kind: 'removePhase'; readonly index: number }
  | { readonly kind: 'movePhase'; readonly index: number; readonly direction: -1 | 1 }
  | { readonly kind: 'changePhaseVersion'; readonly index: number; readonly optionValue: string }
  | { readonly kind: 'renameSeries'; readonly name: string }
  | { readonly kind: 'saveSeries' };

export function sourceDragStateFromCanvasEvent(
  event: CanvasOutputPropertyDragEvent,
  bridgePosition: number | null,
): PhaseBridgeDragState {
  return {
    kind: 'source-output',
    clientX: event.clientX,
    clientY: event.clientY,
    label: `${event.nodeName}.${event.propertyName}`,
    propertyKind: event.propertyKind,
    mappingId: null,
    bridgePosition,
    sourceNodeId: event.nodeId,
    sourceBlueprintNodeId: event.nodeInstanceId,
    sourceNodeName: event.nodeName,
    sourceChannelName: event.channelName,
    sourceProperty: event.propertyName,
  };
}

export function bridgeOutputDragStateFromMapping(
  mapping: DataBridgeMappingDraft,
  clientX: number,
  clientY: number,
): PhaseBridgeDragState {
  return {
    kind: 'bridge-output',
    clientX,
    clientY,
    label: mapping.sourceProperty,
    propertyKind: mapping.propertyKind,
    mappingId: mapping.id,
  };
}
