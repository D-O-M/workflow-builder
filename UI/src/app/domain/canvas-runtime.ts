import { Point } from './node-model';
import { CanvasBackgroundEffect } from './canvas-backdrop';
import { CanvasConnectionPresentation, CanvasDetailTier } from './canvas-render-model';

export type BlueprintCanvasMode = 'edit' | 'readonly' | 'bridge-map' | 'bridge-source' | 'bridge-target';

export interface CanvasViewportInsets {
  readonly top: number;
  readonly right: number;
  readonly bottom: number;
  readonly left: number;
}

export const EMPTY_CANVAS_VIEWPORT_INSETS: CanvasViewportInsets = {
  top: 0,
  right: 0,
  bottom: 0,
  left: 0,
};

export interface CanvasModeCapabilities {
  readonly readOnly: boolean;
  readonly graphEditable: boolean;
  readonly connectionMapping: boolean;
  readonly passiveSummaryPorts: boolean;
  readonly dragConnectionPreview: boolean;
  readonly selectableConnections: boolean;
  readonly viewportCulling: boolean;
}

export interface CanvasViewportState {
  readonly offset: Point;
  readonly zoom: number;
  readonly viewportSize: {
    readonly width: number;
    readonly height: number;
  };
  readonly insets: CanvasViewportInsets;
}

export interface CanvasRuntimeConfig {
  readonly mode: BlueprintCanvasMode;
  readonly backgroundEffect: CanvasBackgroundEffect;
  readonly connectionPresentation: CanvasConnectionPresentation;
  readonly detailTier: CanvasDetailTier;
  readonly viewportInsets: CanvasViewportInsets;
}

export type CanvasInteractionEvent =
  | { readonly kind: 'node-click'; readonly nodeId: number }
  | { readonly kind: 'node-drag'; readonly nodeIds: readonly number[] }
  | { readonly kind: 'connection-drag'; readonly sourceNodeId: number; readonly sourceProperty: string | null }
  | { readonly kind: 'canvas-pan'; readonly offset: Point }
  | { readonly kind: 'marquee-select'; readonly bounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number } };

export interface CanvasRenderContext {
  readonly config: CanvasRuntimeConfig;
  readonly viewport: CanvasViewportState | null;
  readonly hoveredNodeId: number | null;
  readonly hoveredPortKey: string | null;
  readonly hoveredConnectionId: string | null;
}
