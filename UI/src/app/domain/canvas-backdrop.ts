import { Point } from './node-model';

export type CanvasBackgroundEffect = 'plain' | 'stitch';

export interface VisibleWorldBounds {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

export function getVisibleWorldBounds(
  canvasWidth: number,
  canvasHeight: number,
  viewportOffset: Point,
  zoomLevel: number,
): VisibleWorldBounds {
  const safeZoomLevel = Math.max(zoomLevel, 0.0001);

  return {
    left: -viewportOffset.x / safeZoomLevel,
    top: -viewportOffset.y / safeZoomLevel,
    right: (canvasWidth - viewportOffset.x) / safeZoomLevel,
    bottom: (canvasHeight - viewportOffset.y) / safeZoomLevel,
  };
}

export function canvasPointToWorldPoint(
  canvasPoint: Point,
  viewportOffset: Point,
  zoomLevel: number,
): Point {
  const safeZoomLevel = Math.max(zoomLevel, 0.0001);

  return {
    x: (canvasPoint.x - viewportOffset.x) / safeZoomLevel,
    y: (canvasPoint.y - viewportOffset.y) / safeZoomLevel,
  };
}

export function getWorldRadiusFromScreenRadius(
  radiusPx: number,
  zoomLevel: number,
): number {
  return radiusPx / Math.max(zoomLevel, 0.0001);
}

export function getHighlightFalloff(
  distanceSquared: number,
  radiusSquared: number,
): number {
  if (radiusSquared <= 0 || distanceSquared >= radiusSquared) {
    return 0;
  }

  const normalizedDistance = 1 - distanceSquared / radiusSquared;
  return normalizedDistance * normalizedDistance;
}
