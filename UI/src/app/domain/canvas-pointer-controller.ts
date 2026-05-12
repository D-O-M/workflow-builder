import { Point } from './node-model';

export type CanvasPendingPointerTransition = 'none' | 'node-drag' | 'pan';

export interface CanvasPendingClickCandidate {
  readonly startCanvas: Point;
  readonly nodeId: number | null;
  readonly connectionId: string | null;
  readonly shift: boolean;
  readonly toggleSectionKey: string | null;
}

export class CanvasPointerController {
  constructor(private readonly clickTolerance: number) {}

  movedBeyondClickTolerance(start: Point, current: Point): boolean {
    return Math.abs(current.x - start.x) > this.clickTolerance ||
      Math.abs(current.y - start.y) > this.clickTolerance;
  }

  pendingClickTransition(
    pendingClick: CanvasPendingClickCandidate,
    currentCanvasPoint: Point,
    canEditGraph: boolean,
  ): CanvasPendingPointerTransition {
    if (pendingClick.toggleSectionKey !== null ||
      !this.movedBeyondClickTolerance(pendingClick.startCanvas, currentCanvasPoint)) {
      return 'none';
    }

    if (pendingClick.nodeId !== null && !pendingClick.shift && canEditGraph) {
      return 'node-drag';
    }

    if (pendingClick.nodeId === null && pendingClick.connectionId === null) {
      return 'pan';
    }

    return 'none';
  }
}
