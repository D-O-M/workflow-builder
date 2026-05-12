import { ConnectionModel } from '../domain/connection-model';
import { NodeModel, Point, TaskPropertyType } from '../domain/node-model';
import { CanvasBackgroundEffect } from '../domain/canvas-backdrop';
import {
  CanvasBounds,
  CanvasConnectionPresentation,
  CanvasRouteValidationError,
} from '../domain/canvas-render-model';
import {
  BlueprintCanvasMode,
  CanvasViewportInsets,
  EMPTY_CANVAS_VIEWPORT_INSETS,
} from '../domain/canvas-runtime';
import {
  CanvasInputPropertyHit,
  CanvasOutputPropertyDragEvent,
  CanvasPropertyClientPoint,
} from '../domain/canvas-bridge-adapter';
import {
  CanvasValidationFocus,
  CanvasValidationIssueMarker,
} from '../domain/canvas-validation-focus-resolver';

export type {
  BlueprintCanvasMode,
  CanvasViewportInsets,
} from '../domain/canvas-runtime';

export type {
  CanvasInputPropertyHit,
  CanvasOutputPropertyDragEvent,
  CanvasPropertyClientPoint,
} from '../domain/canvas-bridge-adapter';

export type {
  CanvasValidationFocus,
  CanvasValidationIssueMarker,
} from '../domain/canvas-validation-focus-resolver';

export interface CanvasModifierState {
  readonly shift: boolean;
  readonly ctrlOrMeta: boolean;
  readonly space: boolean;
}

export const EMPTY_CANVAS_MODIFIER_STATE: CanvasModifierState = {
  shift: false,
  ctrlOrMeta: false,
  space: false,
};

export interface BlueprintCanvasGraph {
  readonly nodes: readonly NodeModel[];
  readonly connections: readonly ConnectionModel[];
  readonly revision: number;
}

export interface BlueprintCanvasRuntime {
  readonly mode: BlueprintCanvasMode;
  readonly backgroundEffect: CanvasBackgroundEffect;
  readonly connectionPresentation: CanvasConnectionPresentation;
  readonly viewportInsets: CanvasViewportInsets;
}

export const DEFAULT_BLUEPRINT_CANVAS_RUNTIME: BlueprintCanvasRuntime = {
  mode: 'edit',
  backgroundEffect: 'plain',
  connectionPresentation: 'normal',
  viewportInsets: EMPTY_CANVAS_VIEWPORT_INSETS,
};

export interface BlueprintCanvasSelection {
  readonly selectedNodeIds: readonly number[];
  readonly selectedConnectionIds: readonly string[];
  readonly readonlySelectedNodeId: number | null;
  readonly canvasFocused: boolean;
  readonly modifiers: CanvasModifierState;
}

export const EMPTY_BLUEPRINT_CANVAS_SELECTION: BlueprintCanvasSelection = {
  selectedNodeIds: [],
  selectedConnectionIds: [],
  readonlySelectedNodeId: null,
  canvasFocused: false,
  modifiers: EMPTY_CANVAS_MODIFIER_STATE,
};

export interface BlueprintCanvasValidation {
  readonly focus: CanvasValidationFocus | null;
  readonly issueMarkers: readonly CanvasValidationIssueMarker[];
}

export const EMPTY_BLUEPRINT_CANVAS_VALIDATION: BlueprintCanvasValidation = {
  focus: null,
  issueMarkers: [],
};

export interface BlueprintCanvasViewportPort {
  readonly reset: () => void;
  readonly zoomIn: () => void;
  readonly zoomOut: () => void;
  readonly focus: () => void;
  readonly centerOnNode: (nodeId: number) => void;
  readonly centerOnBounds: (bounds: CanvasBounds) => void;
  readonly centerWorld: () => Point | null;
  readonly containsClientPoint: (point: { readonly clientX: number; readonly clientY: number }, tolerance?: number) => boolean;
}

export interface BlueprintCanvasValidationPort {
  readonly reveal: (focus: CanvasValidationFocus | null) => void;
  readonly routeErrors: () => readonly CanvasRouteValidationError[];
}

export interface BlueprintCanvasBridgePort {
  readonly outputPropertyClientPoint: (
    nodeId: number,
    channelName: string | null,
    propertyName: string,
  ) => CanvasPropertyClientPoint | null;
  readonly inputPropertyClientPoint: (nodeId: number, propertyName: string) => CanvasPropertyClientPoint | null;
  readonly hitTestInputPropertyAtClientPoint: (clientX: number, clientY: number) => CanvasInputPropertyHit | null;
}

export interface BlueprintCanvasNodePositionUpdate {
  readonly nodeId: number;
  readonly coordinates: Point;
}

export interface BlueprintCanvasConnectionCreateRequest {
  readonly from: number;
  readonly to: number;
  readonly anchor: number;
  readonly side: 'left';
  readonly position: number;
  readonly upsertDataBridgeInputProperty?: {
    readonly nodeId: number;
    readonly propertyName: string;
    readonly propertyKind: TaskPropertyType;
  } | null;
  readonly propertyMapping?: {
    readonly sourceProperty: string;
    readonly targetProperty: string;
  } | null;
}

export type BlueprintCanvasEvent =
  | { readonly kind: 'focusChanged'; readonly focused: boolean }
  | { readonly kind: 'readonlyNodeActivated'; readonly nodeId: number }
  | { readonly kind: 'nodeSettingsRequested'; readonly nodeId: number }
  | { readonly kind: 'selectOnlyNode'; readonly nodeId: number }
  | { readonly kind: 'toggleNodeSelection'; readonly nodeId: number }
  | { readonly kind: 'selectOnlyConnection'; readonly connectionId: string }
  | { readonly kind: 'toggleConnectionSelection'; readonly connectionId: string }
  | {
      readonly kind: 'selectNodesWithinRect';
      readonly bounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number };
      readonly additive: boolean;
    }
  | { readonly kind: 'clearSelection' }
  | { readonly kind: 'captureHistory' }
  | { readonly kind: 'updateNodePositions'; readonly positions: readonly BlueprintCanvasNodePositionUpdate[] }
  | { readonly kind: 'createConnection'; readonly request: BlueprintCanvasConnectionCreateRequest }
  | {
      readonly kind: 'bridgeOutputPropertyDrag';
      readonly phase: 'started' | 'moved' | 'ended';
      readonly drag: CanvasOutputPropertyDragEvent;
    }
  | { readonly kind: 'viewportChanged' };
