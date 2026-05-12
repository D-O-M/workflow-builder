import { CanvasRenderSurface } from './canvas-draw-surface';
import { CanvasTheme } from './canvas-theme';
import { NodeModel, Side } from './node-model';
import { PathfinderObstacle, PathfinderOptions, Segment, orthogonalRoute, subtractRouteProblemSegments } from './pathfinder';

export interface CanvasConnectionPreviewRequest {
  readonly sx: number;
  readonly sy: number;
  readonly ex: number;
  readonly ey: number;
  readonly obstacles?: readonly PathfinderObstacle[];
  readonly side?: Side;
  readonly destNode?: PathfinderObstacle;
  readonly startSide?: Side;
  readonly startNode?: PathfinderObstacle;
  readonly pathfinderOptions: PathfinderOptions;
}

export class CanvasConnectionPreviewRenderer {
  constructor(private readonly surface: CanvasRenderSurface) {}

  drawOrthogonalLine(request: CanvasConnectionPreviewRequest): void {
    const route = orthogonalRoute(
      request.sx,
      request.sy,
      request.ex,
      request.ey,
      request.pathfinderOptions,
      [...(request.obstacles ?? [])],
      request.side,
      request.destNode,
      request.startSide,
      request.startNode,
    );
    const segments = route.segments;
    if (segments.length === 0) {
      return;
    }

    this.surface.ctx.save();
    this.surface.ctx.strokeStyle = route.status === 'obstacleViolation'
      ? CanvasTheme.validation.error.stroke
      : CanvasTheme.connection.selected;
    this.surface.ctx.fillStyle = this.surface.ctx.strokeStyle;
    this.surface.ctx.lineWidth = route.status === 'obstacleViolation' ? 5.4 : 6.2;
    this.surface.ctx.lineJoin = 'round';
    this.surface.ctx.lineCap = 'butt';

    if (route.problemSegments.length > 0) {
      for (const segment of subtractRouteProblemSegments(segments, route.problemSegments)) {
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(segment.x1, segment.y1);
        this.surface.ctx.lineTo(segment.x2, segment.y2);
        this.surface.ctx.stroke();
      }

      this.surface.ctx.setLineDash([8, 6]);
      this.surface.ctx.lineWidth = Math.max(this.surface.ctx.lineWidth, 3);
      for (const problem of route.problemSegments) {
        this.surface.ctx.beginPath();
        this.surface.ctx.moveTo(problem.x1, problem.y1);
        this.surface.ctx.lineTo(problem.x2, problem.y2);
        this.surface.ctx.stroke();
      }
      this.surface.ctx.setLineDash([]);
    } else {
      this.drawRoundedOrthogonalSegments(segments);
    }

    this.surface.ctx.restore();
  }

  pathfinderOptions(gridSize: number): PathfinderOptions {
    return {
      gridSize,
      nodeMargin: NodeModel.buffer,
      obstacleBuffer: Math.max(NodeModel.buffer / gridSize, 1),
    };
  }

  private drawRoundedOrthogonalSegments(segments: readonly Segment[]): void {
    if (segments.length === 0) {
      return;
    }

    const points = [
      { x: segments[0]!.x1, y: segments[0]!.y1 },
      ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
    ];
    if (points.length < 3) {
      this.surface.ctx.beginPath();
      this.surface.ctx.moveTo(segments[0]!.x1, segments[0]!.y1);
      this.surface.ctx.lineTo(segments[0]!.x2, segments[0]!.y2);
      this.surface.ctx.stroke();
      return;
    }

    const cornerRadius = 7;
    this.surface.ctx.beginPath();
    this.surface.ctx.moveTo(points[0]!.x, points[0]!.y);

    for (let index = 1; index < points.length - 1; index += 1) {
      const previous = points[index - 1]!;
      const current = points[index]!;
      const next = points[index + 1]!;

      if (
        (previous.x === current.x && current.x === next.x) ||
        (previous.y === current.y && current.y === next.y)
      ) {
        this.surface.ctx.lineTo(current.x, current.y);
        continue;
      }

      const previousDistance = Math.abs(current.x - previous.x) + Math.abs(current.y - previous.y);
      const nextDistance = Math.abs(next.x - current.x) + Math.abs(next.y - current.y);
      const radius = Math.min(cornerRadius, previousDistance / 2, nextDistance / 2);
      if (radius <= 0) {
        this.surface.ctx.lineTo(current.x, current.y);
        continue;
      }

      const startRatio = radius / previousDistance;
      const endRatio = radius / nextDistance;
      const cornerStart = {
        x: current.x + (previous.x - current.x) * startRatio,
        y: current.y + (previous.y - current.y) * startRatio,
      };
      const cornerEnd = {
        x: current.x + (next.x - current.x) * endRatio,
        y: current.y + (next.y - current.y) * endRatio,
      };
      this.surface.ctx.lineTo(cornerStart.x, cornerStart.y);
      this.surface.ctx.quadraticCurveTo(current.x, current.y, cornerEnd.x, cornerEnd.y);
    }

    const lastPoint = points[points.length - 1]!;
    this.surface.ctx.lineTo(lastPoint.x, lastPoint.y);
    this.surface.ctx.stroke();
  }
}
