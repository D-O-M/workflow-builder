import { Signal, signal } from '@angular/core';
import { CanvasBackgroundEffect } from './canvas-backdrop';
import { CanvasBackdropRenderer } from './canvas-backdrop-renderer';
import { CanvasConnectionPreviewRenderer } from './canvas-connection-preview-renderer';
import { CanvasConnectionRenderer, CanvasConnectionRenderOptions } from './canvas-connection-renderer';
import { CanvasDrawSurface } from './canvas-draw-surface';
import {
  CanvasDetailTier,
  CanvasPortConnectivityIndex,
  CanvasValidationSeverity,
  RenderedConnection,
  RenderedNode,
} from './canvas-render-model';
import {
  CanvasNodeRenderer,
  CanvasNodeRenderDrag,
  CanvasNodeRenderOptions,
} from './canvas-node-renderer';
import { CanvasOverlayRenderer } from './canvas-overlay-renderer';
import { CanvasTheme } from './canvas-theme';
import { NodeModel, Point, Side } from './node-model';
import { PathfinderObstacle, PathfinderOptions } from './pathfinder';

export type { CanvasNodeIssueSummary, CanvasPropertyIssueSummary } from './canvas-node-render-types';

export class CanvasWrapper {
  readonly viewportOffset = signal<Point>({ x: 0, y: 0 });
  readonly zoomLevel = signal(1);
  readonly fontReady: Signal<boolean>;

  private readonly surface: CanvasDrawSurface;
  private readonly backdropRenderer: CanvasBackdropRenderer;
  private readonly connectionRenderer: CanvasConnectionRenderer;
  private readonly nodeRenderer: CanvasNodeRenderer;
  private readonly overlayRenderer: CanvasOverlayRenderer;
  private readonly connectionPreviewRenderer: CanvasConnectionPreviewRenderer;

  constructor(
    public readonly ctx: CanvasRenderingContext2D,
    private readonly gridSize: number,
  ) {
    this.surface = new CanvasDrawSurface(ctx);
    this.fontReady = this.surface.fontReady;
    this.backdropRenderer = new CanvasBackdropRenderer(this.surface);
    this.connectionRenderer = new CanvasConnectionRenderer(this);
    this.nodeRenderer = new CanvasNodeRenderer(this.surface);
    this.overlayRenderer = new CanvasOverlayRenderer(this.surface);
    this.connectionPreviewRenderer = new CanvasConnectionPreviewRenderer(this.surface);
  }

  clear(width: number, height: number): void {
    this.ctx.clearRect(0, 0, width, height);
    this.ctx.strokeStyle = CanvasTheme.connection.stroke;
    this.ctx.fillStyle = CanvasTheme.connection.stroke;
    this.ctx.lineWidth = 2;
    this.surface.setFont(600, 14);
    this.ctx.textBaseline = 'alphabetic';
  }

  drawBackdrop(
    canvasWidth: number,
    canvasHeight: number,
    effect: CanvasBackgroundEffect,
    hoveredCanvasPoint: Point | null,
  ): void {
    this.backdropRenderer.drawBackdrop({
      canvasWidth,
      canvasHeight,
      effect,
      hoveredCanvasPoint,
      viewportOffset: this.viewportOffset(),
      zoomLevel: this.zoomLevel(),
    });
  }

  drawConnections(
    connections: readonly RenderedConnection[],
    selectedConnectionIds: ReadonlySet<string> = new Set<string>(),
    options?: CanvasConnectionRenderOptions,
  ): void {
    this.connectionRenderer.drawConnections(connections, selectedConnectionIds, options);
  }

  drawNodes(
    nodes: readonly RenderedNode[],
    portConnectivity: CanvasPortConnectivityIndex,
    drag: CanvasNodeRenderDrag | null,
    hovered: number | null,
    hoveredPortKey: string | null,
    selectedNodeIds: ReadonlySet<number> = new Set<number>(),
    options?: CanvasNodeRenderOptions,
  ): void {
    this.nodeRenderer.drawNodes({
      nodes,
      portConnectivity,
      drag,
      hoveredNodeId: hovered,
      hoveredPortKey,
      selectedNodeIds,
      options,
    });
  }

  drawOrthogonalLine(
    sx: number,
    sy: number,
    ex: number,
    ey: number,
    obstacles: PathfinderObstacle[] = [],
    side?: Side,
    destNode?: PathfinderObstacle,
    startSide?: Side,
    startNode?: PathfinderObstacle,
  ): void {
    this.connectionPreviewRenderer.drawOrthogonalLine({
      sx,
      sy,
      ex,
      ey,
      obstacles,
      side,
      destNode,
      startSide,
      startNode,
      pathfinderOptions: this.pathfinderOptions(),
    });
  }

  drawMarqueeRect(left: number, top: number, right: number, bottom: number): void {
    this.overlayRenderer.drawMarqueeRect(left, top, right, bottom);
  }

  drawFailureTooltip(node: RenderedNode, message: string): void {
    this.overlayRenderer.drawFailureTooltip(node, message);
  }

  drawInteractionTooltip(label: string, point: Point): void {
    this.overlayRenderer.drawInteractionTooltip(label, point, {
      zoomLevel: this.zoomLevel(),
    });
  }

  drawWrappedText(text: string, x: number, y: number, maxWidth: number): void {
    this.surface.drawWrappedText(text, x, y, maxWidth);
  }

  drawEllipsizedText(text: string, x: number, y: number, maxWidth: number): string {
    return this.surface.drawEllipsizedText(text, x, y, maxWidth);
  }

  snap(value: number): number {
    return Math.round(value / this.gridSize) * this.gridSize;
  }

  pathfinderOptions(): PathfinderOptions {
    return {
      gridSize: this.gridSize,
      nodeMargin: NodeModel.buffer,
      obstacleBuffer: Math.max(NodeModel.buffer / this.gridSize, 1),
    };
  }
}
