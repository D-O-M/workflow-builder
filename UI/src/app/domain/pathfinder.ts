import { Side } from './node-model';

export interface PathfinderOptions {
    gridSize: number;
    nodeMargin: number;
    obstacleBuffer?: number;
}

export interface Point { x: number; y: number }
export interface Segment { x1: number; y1: number; x2: number; y2: number }
export type RouteStatus = 'clear' | 'obstacleViolation';

export interface RouteProblemSegment extends Segment {
    obstacle: PathfinderObstacle;
}

export interface OrthogonalRouteResult {
    status: RouteStatus;
    segments: Segment[];
    problemSegments: RouteProblemSegment[];
    validationMessage?: string;
}

export interface PathfinderObstacle {
    x: number;
    y: number;
    width: number;
    height: number;
    contains(x: number, y: number): boolean;
    containsBuffered(x: number, y: number, buffer: number): boolean;
    snapPointFixedSide(x: number, y: number, side: Side): { x: number; y: number; side: Side; pos: number };
}

type BufferedObstacleRect = ReturnType<typeof bufferedRect>;

const ROUTE_OBSTACLE_VALIDATION_MESSAGE = "Connection crosses another node's reserved routing buffer.";

function uniqueSorted(values: number[]): number[] {
    return [...new Set(values.map((value) => Math.round(value * 1000) / 1000))]
        .sort((left, right) => left - right);
}

function sideOffset(point: Point, side: Side | undefined, distance: number): Point {
    if (!side || distance === 0) {
        return { ...point };
    }

    switch (side) {
        case 'left':
            return { x: point.x - distance, y: point.y };
        case 'right':
            return { x: point.x + distance, y: point.y };
        case 'top':
            return { x: point.x, y: point.y - distance };
        default:
            return { x: point.x, y: point.y + distance };
    }
}

function normalizeDestinationPoint(
    x: number,
    y: number,
    side: Side | undefined,
    destNode: PathfinderObstacle | undefined,
    pad: number,
): Point {
    if (!destNode || !side || !destNode.contains(x, y) || !destNode.containsBuffered(x, y, pad)) {
        return { x, y };
    }

    const snapped = destNode.snapPointFixedSide(x, y, side);
    return { x: snapped.x, y: snapped.y };
}

function segmentLength(segment: Segment): number {
    return Math.abs(segment.x2 - segment.x1) + Math.abs(segment.y2 - segment.y1);
}

function directionOfSegment(segment: Segment): Side | null {
    if (segment.x1 === segment.x2 && segment.y1 !== segment.y2) {
        return segment.y2 < segment.y1 ? 'top' : 'bottom';
    }

    if (segment.y1 === segment.y2 && segment.x1 !== segment.x2) {
        return segment.x2 < segment.x1 ? 'left' : 'right';
    }

    return null;
}

function oppositeSide(side: Side): Side {
    switch (side) {
        case 'left':
            return 'right';
        case 'right':
            return 'left';
        case 'top':
            return 'bottom';
        case 'bottom':
            return 'top';
    }
}

function pointKey(point: Point): string {
    return `${point.x},${point.y}`;
}

function segmentKey(segment: Segment): string {
    return `${segment.x1},${segment.y1},${segment.x2},${segment.y2}`;
}

function compactSegments(segments: Segment[]): Segment[] {
    const compacted: Segment[] = [];
    for (const segment of segments) {
        if (segment.x1 !== segment.x2 && segment.y1 !== segment.y2) {
            continue;
        }

        if (segment.x1 === segment.x2 && segment.y1 === segment.y2) {
            continue;
        }

        const previous = compacted[compacted.length - 1];
        if (previous && previous.x2 === segment.x1 && previous.y2 === segment.y1) {
            if (previous.y1 === previous.y2 && segment.y1 === segment.y2 && previous.y1 === segment.y1) {
                previous.x2 = segment.x2;
                if (previous.x1 === previous.x2 && previous.y1 === previous.y2) {
                    compacted.pop();
                }
                continue;
            }

            if (previous.x1 === previous.x2 && segment.x1 === segment.x2 && previous.x1 === segment.x1) {
                previous.y2 = segment.y2;
                if (previous.x1 === previous.x2 && previous.y1 === previous.y2) {
                    compacted.pop();
                }
                continue;
            }
        }

        compacted.push({ ...segment });
    }

    return compacted;
}

function pointsToSegments(points: Point[]): Segment[] {
    const segments: Segment[] = [];
    for (let index = 1; index < points.length; index += 1) {
        const previous = points[index - 1]!;
        const current = points[index]!;
        if (previous.x !== current.x && previous.y !== current.y) {
            return [];
        }

        segments.push({
            x1: previous.x,
            y1: previous.y,
            x2: current.x,
            y2: current.y,
        });
    }

    return compactSegments(segments);
}

function routeSegmentsToPoints(segments: readonly Segment[]): Point[] {
    if (segments.length === 0) {
        return [];
    }

    return [
        { x: segments[0]!.x1, y: segments[0]!.y1 },
        ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
    ];
}

function pointsEqual(left: Point, right: Point): boolean {
    return left.x === right.x && left.y === right.y;
}

function pointsCollinear(previous: Point, current: Point, next: Point): boolean {
    return (previous.x === current.x && current.x === next.x) ||
        (previous.y === current.y && current.y === next.y);
}

function normalizePointRoute(points: readonly Point[]): Point[] {
    let normalized = points.map((point) => ({ ...point }));
    let changed = true;

    while (changed) {
        changed = false;

        const deduped: Point[] = [];
        for (const point of normalized) {
            const previous = deduped[deduped.length - 1];
            if (previous && pointsEqual(previous, point)) {
                changed = true;
                continue;
            }

            deduped.push(point);
        }
        normalized = deduped;

        const firstIndexByKey = new Map<string, number>();
        const loopClipped: Point[] = [];
        for (const point of normalized) {
            const key = pointKey(point);
            const existingIndex = firstIndexByKey.get(key);
            if (existingIndex !== undefined) {
                for (let index = loopClipped.length - 1; index > existingIndex; index -= 1) {
                    firstIndexByKey.delete(pointKey(loopClipped[index]!));
                    loopClipped.pop();
                }
                changed = true;
                continue;
            }

            firstIndexByKey.set(key, loopClipped.length);
            loopClipped.push(point);
        }
        normalized = loopClipped;

        const compacted: Point[] = [];
        for (let index = 0; index < normalized.length; index += 1) {
            const point = normalized[index]!;
            const previous = compacted[compacted.length - 1];
            const next = normalized[index + 1];
            if (previous && next && pointsCollinear(previous, point, next)) {
                changed = true;
                continue;
            }

            compacted.push(point);
        }
        normalized = compacted;
    }

    return normalized;
}

function normalizeSegments(segments: readonly Segment[]): Segment[] {
    if (segments.length <= 1) {
        return compactSegments(segments.map((segment) => ({ ...segment })));
    }

    return pointsToSegments(normalizePointRoute(routeSegmentsToPoints(segments)));
}

function segmentFromPoints(start: Point, end: Point): Segment[] {
    if (start.x === end.x && start.y === end.y) {
        return [];
    }

    if (start.x !== end.x && start.y !== end.y) {
        return [];
    }

    return [{ x1: start.x, y1: start.y, x2: end.x, y2: end.y }];
}

function bufferedRect(obstacle: PathfinderObstacle, buffer: number) {
    return {
        obstacle,
        left: obstacle.x - buffer,
        right: obstacle.x + obstacle.width + buffer,
        top: obstacle.y - buffer,
        bottom: obstacle.y + obstacle.height + buffer,
    };
}

function buildBufferedRects(
    obstacles: PathfinderObstacle[],
    startNode: PathfinderObstacle | undefined,
    destNode: PathfinderObstacle | undefined,
    buffer: number,
): BufferedObstacleRect[] {
    const unique = new Set<PathfinderObstacle>();
    for (const obstacle of obstacles) {
        unique.add(obstacle);
    }

    if (startNode) {
        unique.add(startNode);
    }

    if (destNode) {
        unique.add(destNode);
    }

    return [...unique].map((obstacle) => bufferedRect(obstacle, buffer));
}

function segmentIntersectsRectInterior(segment: Segment, rect: ReturnType<typeof bufferedRect>): boolean {
    if (segment.y1 === segment.y2) {
        const y = segment.y1;
        if (y <= rect.top || y >= rect.bottom) {
            return false;
        }

        const start = Math.min(segment.x1, segment.x2);
        const end = Math.max(segment.x1, segment.x2);
        return Math.max(start, rect.left) < Math.min(end, rect.right);
    }

    if (segment.x1 === segment.x2) {
        const x = segment.x1;
        if (x <= rect.left || x >= rect.right) {
            return false;
        }

        const start = Math.min(segment.y1, segment.y2);
        const end = Math.max(segment.y1, segment.y2);
        return Math.max(start, rect.top) < Math.min(end, rect.bottom);
    }

    return true;
}

function problemSegmentsForRect(segment: Segment, rect: ReturnType<typeof bufferedRect>): RouteProblemSegment[] {
    if (!segmentIntersectsRectInterior(segment, rect)) {
        return [];
    }

    if (segment.y1 === segment.y2) {
        const start = Math.min(segment.x1, segment.x2);
        const end = Math.max(segment.x1, segment.x2);
        const overlapStart = Math.max(start, rect.left);
        const overlapEnd = Math.min(end, rect.right);
        return [{
            x1: segment.x1 <= segment.x2 ? overlapStart : overlapEnd,
            y1: segment.y1,
            x2: segment.x1 <= segment.x2 ? overlapEnd : overlapStart,
            y2: segment.y2,
            obstacle: rect.obstacle,
        }];
    }

    if (segment.x1 === segment.x2) {
        const start = Math.min(segment.y1, segment.y2);
        const end = Math.max(segment.y1, segment.y2);
        const overlapStart = Math.max(start, rect.top);
        const overlapEnd = Math.min(end, rect.bottom);
        return [{
            x1: segment.x1,
            y1: segment.y1 <= segment.y2 ? overlapStart : overlapEnd,
            x2: segment.x2,
            y2: segment.y1 <= segment.y2 ? overlapEnd : overlapStart,
            obstacle: rect.obstacle,
        }];
    }

    return [];
}

function problemSegmentsForRoute(segments: readonly Segment[], rects: readonly BufferedObstacleRect[]): RouteProblemSegment[] {
    const problemSegments: RouteProblemSegment[] = [];
    const seen = new Set<string>();

    for (const segment of segments) {
        for (const rect of rects) {
            for (const problem of problemSegmentsForRect(segment, rect)) {
                const key = `${segmentKey(problem)}:${rect.left}:${rect.top}:${rect.right}:${rect.bottom}`;
                if (seen.has(key)) {
                    continue;
                }

                seen.add(key);
                problemSegments.push(problem);
            }
        }
    }

    return problemSegments;
}

function routeClear(segments: readonly Segment[], rects: readonly BufferedObstacleRect[]): boolean {
    return problemSegmentsForRoute(segments, rects).length === 0;
}

function routeLength(segments: readonly Segment[]): number {
    return segments.reduce((total, segment) => total + segmentLength(segment), 0);
}

function routeTurnCount(segments: readonly Segment[]): number {
    let turns = 0;
    let previousAxis: 'horizontal' | 'vertical' | null = null;

    for (const segment of segments) {
        const axis = segment.y1 === segment.y2
            ? 'horizontal'
            : segment.x1 === segment.x2
                ? 'vertical'
                : null;
        if (!axis) {
            continue;
        }

        if (previousAxis && previousAxis !== axis) {
            turns += 1;
        }

        previousAxis = axis;
    }

    return turns;
}

function routeBoundaryBacktrackPenalty(
    segments: readonly Segment[],
    startSide?: Side,
    endSide?: Side,
): number {
    let penalty = 0;
    const firstDirection = segments[0] ? directionOfSegment(segments[0]!) : null;
    const lastDirection = segments[segments.length - 1] ? directionOfSegment(segments[segments.length - 1]!) : null;

    if (startSide && firstDirection === oppositeSide(startSide)) {
        penalty += 1;
    }

    if (endSide && lastDirection === endSide) {
        penalty += 1;
    }

    for (let index = 1; index < segments.length; index += 1) {
        const previousDirection = directionOfSegment(segments[index - 1]!);
        const direction = directionOfSegment(segments[index]!);
        if (previousDirection && direction && direction === oppositeSide(previousDirection)) {
            penalty += 1;
        }
    }

    return penalty;
}

function compareRouteCandidates(
    left: readonly Segment[],
    right: readonly Segment[],
    startSide?: Side,
    endSide?: Side,
): number {
    const boundaryDelta = routeBoundaryBacktrackPenalty(left, startSide, endSide) -
        routeBoundaryBacktrackPenalty(right, startSide, endSide);
    if (boundaryDelta !== 0) {
        return boundaryDelta;
    }

    const turnDelta = routeTurnCount(left) - routeTurnCount(right);
    if (turnDelta !== 0) {
        return turnDelta;
    }

    const lengthDelta = routeLength(left) - routeLength(right);
    if (lengthDelta !== 0) {
        return lengthDelta;
    }

    return left.length - right.length;
}

function uniqueCandidateRoutes(routes: Segment[][]): Segment[][] {
    const seen = new Set<string>();
    const unique: Segment[][] = [];

    for (const route of routes) {
        const normalized = normalizeSegments(route);
        const key = normalized.map(segmentKey).join('|');
        if (seen.has(key)) {
            continue;
        }

        seen.add(key);
        unique.push(normalized);
    }

    return unique;
}

function candidateSegmentsFromPoints(points: Point[]): Segment[] {
    return pointsToSegments(normalizePointRoute(points));
}

function buildVisibilityCandidates(
    start: Point,
    end: Point,
    rects: readonly BufferedObstacleRect[],
    grid: number,
    nodeMargin: number,
): Segment[][] {
    const candidates: Segment[][] = [];
    const addCandidate = (points: Point[]) => {
        const segments = candidateSegmentsFromPoints(points);
        if (segments.length > 0 || (start.x === end.x && start.y === end.y)) {
            candidates.push(segments);
        }
    };

    if (start.x === end.x || start.y === end.y) {
        addCandidate([start, end]);
    }

    addCandidate([start, { x: end.x, y: start.y }, end]);
    addCandidate([start, { x: start.x, y: end.y }, end]);

    const minX = Math.min(start.x, end.x);
    const maxX = Math.max(start.x, end.x);
    const minY = Math.min(start.y, end.y);
    const maxY = Math.max(start.y, end.y);
    const corridorPadding = grid * 6;
    const relevantRects = rects.filter((rect) =>
        rect.right >= minX - corridorPadding &&
        rect.left <= maxX + corridorPadding &&
        rect.bottom >= minY - corridorPadding &&
        rect.top <= maxY + corridorPadding,
    );
    const midpointX = (start.x + end.x) / 2;
    const midpointY = (start.y + end.y) / 2;

    const xLanes: number[] = [start.x, end.x];
    const yLanes: number[] = [start.y, end.y];
    const escape = Math.max(nodeMargin * 1.5, grid * 4);
    xLanes.push(start.x - escape, start.x + escape, end.x - escape, end.x + escape, midpointX - escape, midpointX + escape);
    yLanes.push(start.y - escape, start.y + escape, end.y - escape, end.y + escape, midpointY - escape, midpointY + escape);
    if (relevantRects.length > 0) {
        xLanes.push(
            Math.min(start.x, end.x, ...relevantRects.map((rect) => rect.left)) - grid,
            Math.max(start.x, end.x, ...relevantRects.map((rect) => rect.right)) + grid,
        );
        yLanes.push(
            Math.min(start.y, end.y, ...relevantRects.map((rect) => rect.top)) - grid,
            Math.max(start.y, end.y, ...relevantRects.map((rect) => rect.bottom)) + grid,
        );
    }

    for (const rect of relevantRects) {
        xLanes.push(rect.left - grid, rect.right + grid);
        yLanes.push(rect.top - grid, rect.bottom + grid);
    }

    const selectedXLanes = uniqueSorted(xLanes)
        .sort((left, right) => Math.abs(left - midpointX) - Math.abs(right - midpointX))
        .slice(0, 28);
    const selectedYLanes = uniqueSorted(yLanes)
        .sort((left, right) => Math.abs(left - midpointY) - Math.abs(right - midpointY))
        .slice(0, 28);

    for (const x of selectedXLanes) {
        addCandidate([start, { x, y: start.y }, { x, y: end.y }, end]);
    }

    for (const y of selectedYLanes) {
        addCandidate([start, { x: start.x, y }, { x: end.x, y }, end]);
    }

    const selectedCorridorPairs = selectedXLanes
        .flatMap((x) => selectedYLanes.map((y) => ({ x, y })))
        .sort((left, right) =>
            (Math.abs(left.x - midpointX) + Math.abs(left.y - midpointY)) -
            (Math.abs(right.x - midpointX) + Math.abs(right.y - midpointY)))
        .slice(0, 220);

    for (const { x, y } of selectedCorridorPairs) {
        addCandidate([start, { x, y: start.y }, { x, y }, { x: end.x, y }, end]);
        addCandidate([start, { x: start.x, y }, { x, y }, { x, y: end.y }, end]);
    }

    return uniqueCandidateRoutes(candidates);
}

function selectBestClearCandidate(
    start: Point,
    end: Point,
    rects: readonly BufferedObstacleRect[],
    grid: number,
    nodeMargin: number,
    startSide?: Side,
    endSide?: Side,
): Segment[] | null {
    let best: Segment[] | null = null;

    for (const candidate of buildVisibilityCandidates(start, end, rects, grid, nodeMargin)) {
        if (!routeClear(candidate, rects)) {
            continue;
        }

        if (!best || compareRouteCandidates(candidate, best, startSide, endSide) < 0) {
            best = candidate;
        }
    }

    return best;
}

function fallbackCandidates(start: Point, end: Point, nodeMargin: number): Segment[][] {
    const candidates: Segment[][] = [];
    if (start.x === end.x || start.y === end.y) {
        candidates.push(candidateSegmentsFromPoints([start, end]));
    }

    candidates.push(candidateSegmentsFromPoints([start, { x: end.x, y: start.y }, end]));
    candidates.push(candidateSegmentsFromPoints([start, { x: start.x, y: end.y }, end]));
    const escape = Math.max(nodeMargin * 1.5, 40);
    for (const y of uniqueSorted([start.y - escape, start.y + escape, end.y - escape, end.y + escape])) {
        candidates.push(candidateSegmentsFromPoints([start, { x: start.x, y }, { x: end.x, y }, end]));
    }

    for (const x of uniqueSorted([start.x - escape, start.x + escape, end.x - escape, end.x + escape])) {
        candidates.push(candidateSegmentsFromPoints([start, { x, y: start.y }, { x, y: end.y }, end]));
    }

    return uniqueCandidateRoutes(candidates);
}

function selectFallbackRoute(
    start: Point,
    end: Point,
    rects: readonly BufferedObstacleRect[],
    nodeMargin: number,
    startSide?: Side,
    endSide?: Side,
): Segment[] {
    let best = fallbackCandidates(start, end, nodeMargin)[0] ?? [];
    let bestProblems = problemSegmentsForRoute(best, rects);

    for (const candidate of fallbackCandidates(start, end, nodeMargin).slice(1)) {
        const candidateProblems = problemSegmentsForRoute(candidate, rects);
        const problemLengthDelta = routeLength(candidateProblems) - routeLength(bestProblems);
        if (problemLengthDelta < 0) {
            best = candidate;
            bestProblems = candidateProblems;
            continue;
        }

        if (problemLengthDelta === 0 && compareRouteCandidates(candidate, best, startSide, endSide) < 0) {
            best = candidate;
            bestProblems = candidateProblems;
        }
    }

    return best;
}

function stitchRoute(start: Point, startSafe: Point, middleSegments: readonly Segment[], endSafe: Point, end: Point): Segment[] {
    return [
        ...segmentFromPoints(start, startSafe),
        ...middleSegments.map((segment) => ({ ...segment })),
        ...segmentFromPoints(endSafe, end),
    ];
}

function problemSegmentsForStitchedRoute(
    start: Point,
    startSafe: Point,
    middleSegments: readonly Segment[],
    endSafe: Point,
    end: Point,
    rects: readonly BufferedObstacleRect[],
    startNode: PathfinderObstacle | undefined,
    destNode: PathfinderObstacle | undefined,
): RouteProblemSegment[] {
    return [
        ...problemSegmentsForRoute(
            segmentFromPoints(start, startSafe),
            rects.filter((rect) => rect.obstacle !== startNode),
        ),
        ...problemSegmentsForRoute(middleSegments, rects),
        ...problemSegmentsForRoute(
            segmentFromPoints(endSafe, end),
            rects.filter((rect) => rect.obstacle !== destNode),
        ),
    ];
}

function routeTooComplex(clearRoute: readonly Segment[], fallbackRoute: readonly Segment[]): boolean {
    const clearTurns = routeTurnCount(clearRoute);
    const fallbackTurns = routeTurnCount(fallbackRoute);
    if (clearTurns > fallbackTurns + 2) {
        return true;
    }

    const clearLength = routeLength(clearRoute);
    const fallbackLength = routeLength(fallbackRoute);
    return clearTurns > fallbackTurns && clearLength > fallbackLength * 1.8;
}

export function subtractRouteProblemSegments(
    segments: readonly Segment[],
    problemSegments: readonly Segment[],
): Segment[] {
    const safeSegments: Segment[] = [];

    for (const segment of segments) {
        const length = segmentLength(segment);
        if (length === 0) {
            continue;
        }

        const intervals = mergeIntervals(
            problemSegments
                .map((problem) => overlapInterval(segment, problem))
                .filter((interval): interval is [number, number] => !!interval),
        );
        let cursor = 0;

        for (const [start, end] of intervals) {
            if (cursor < start) {
                safeSegments.push(subSegment(segment, cursor, start));
            }

            cursor = Math.max(cursor, end);
        }

        if (cursor < length) {
            safeSegments.push(subSegment(segment, cursor, length));
        }
    }

    return safeSegments;
}

function overlapInterval(segment: Segment, problem: Segment): [number, number] | null {
    if (segment.y1 === segment.y2 && problem.y1 === problem.y2 && segment.y1 === problem.y1) {
        const segmentMin = Math.min(segment.x1, segment.x2);
        const segmentMax = Math.max(segment.x1, segment.x2);
        const problemMin = Math.min(problem.x1, problem.x2);
        const problemMax = Math.max(problem.x1, problem.x2);
        const overlapStart = Math.max(segmentMin, problemMin);
        const overlapEnd = Math.min(segmentMax, problemMax);
        if (overlapStart >= overlapEnd) {
            return null;
        }

        return segment.x2 >= segment.x1
            ? [overlapStart - segment.x1, overlapEnd - segment.x1]
            : [segment.x1 - overlapEnd, segment.x1 - overlapStart];
    }

    if (segment.x1 === segment.x2 && problem.x1 === problem.x2 && segment.x1 === problem.x1) {
        const segmentMin = Math.min(segment.y1, segment.y2);
        const segmentMax = Math.max(segment.y1, segment.y2);
        const problemMin = Math.min(problem.y1, problem.y2);
        const problemMax = Math.max(problem.y1, problem.y2);
        const overlapStart = Math.max(segmentMin, problemMin);
        const overlapEnd = Math.min(segmentMax, problemMax);
        if (overlapStart >= overlapEnd) {
            return null;
        }

        return segment.y2 >= segment.y1
            ? [overlapStart - segment.y1, overlapEnd - segment.y1]
            : [segment.y1 - overlapEnd, segment.y1 - overlapStart];
    }

    return null;
}

function mergeIntervals(intervals: [number, number][]): [number, number][] {
    if (intervals.length === 0) {
        return [];
    }

    intervals.sort(([leftStart], [rightStart]) => leftStart - rightStart);
    const merged: [number, number][] = [intervals[0]!];
    for (const [start, end] of intervals.slice(1)) {
        const current = merged[merged.length - 1]!;
        if (start <= current[1]) {
            current[1] = Math.max(current[1], end);
        } else {
            merged.push([start, end]);
        }
    }

    return merged;
}

function subSegment(segment: Segment, startDistance: number, endDistance: number): Segment {
    if (segment.y1 === segment.y2) {
        const direction = segment.x2 >= segment.x1 ? 1 : -1;
        return {
            x1: segment.x1 + startDistance * direction,
            y1: segment.y1,
            x2: segment.x1 + endDistance * direction,
            y2: segment.y2,
        };
    }

    const direction = segment.y2 >= segment.y1 ? 1 : -1;
    return {
        x1: segment.x1,
        y1: segment.y1 + startDistance * direction,
        x2: segment.x2,
        y2: segment.y1 + endDistance * direction,
    };
}

export function orthogonalRoute(
    startingX: number,
    startingY: number,
    endingX: number,
    endingY: number,
    opts: PathfinderOptions,
    obstacles: PathfinderObstacle[] = [],
    side?: Side,
    destNode?: PathfinderObstacle,
    startSide?: Side,
    startNode?: PathfinderObstacle,
): OrthogonalRouteResult {
    const pad = opts.nodeMargin;
    const start = { x: startingX, y: startingY };
    const end = normalizeDestinationPoint(endingX, endingY, side, destNode, pad);
    const startSafe = sideOffset(start, startSide, pad);
    const endSafe = sideOffset(end, side, pad);
    const rects = buildBufferedRects(obstacles, startNode, destNode, pad);

    const fallbackMiddleRoute = selectFallbackRoute(startSafe, endSafe, rects, pad, startSide, side);
    const fallbackStitchedRoute = stitchRoute(start, startSafe, fallbackMiddleRoute, endSafe, end);
    const fallbackProblemSegments = problemSegmentsForStitchedRoute(
        start,
        startSafe,
        fallbackMiddleRoute,
        endSafe,
        end,
        rects,
        startNode,
        destNode,
    );
    const clearMiddleRoute = selectBestClearCandidate(startSafe, endSafe, rects, opts.gridSize, pad, startSide, side);
    if (clearMiddleRoute) {
        const clearStitchedRoute = stitchRoute(start, startSafe, clearMiddleRoute, endSafe, end);
        if (fallbackProblemSegments.length > 0 && routeTooComplex(clearStitchedRoute, fallbackStitchedRoute)) {
            return {
                status: 'obstacleViolation',
                segments: fallbackStitchedRoute,
                problemSegments: fallbackProblemSegments,
                validationMessage: ROUTE_OBSTACLE_VALIDATION_MESSAGE,
            };
        }

        const problemSegments = problemSegmentsForStitchedRoute(
            start,
            startSafe,
            clearMiddleRoute,
            endSafe,
            end,
            rects,
            startNode,
            destNode,
        );
        if (problemSegments.length > 0) {
            return {
                status: 'obstacleViolation',
                segments: clearStitchedRoute,
                problemSegments,
                validationMessage: ROUTE_OBSTACLE_VALIDATION_MESSAGE,
            };
        }

        return {
            status: 'clear',
            segments: clearStitchedRoute,
            problemSegments: [],
        };
    }

    if (fallbackProblemSegments.length === 0) {
        return {
            status: 'clear',
            segments: fallbackStitchedRoute,
            problemSegments: [],
        };
    }

    return {
        status: 'obstacleViolation',
        segments: fallbackStitchedRoute,
        problemSegments: fallbackProblemSegments,
        validationMessage: ROUTE_OBSTACLE_VALIDATION_MESSAGE,
    };
}

export function orthogonalSegments(
    startingX: number,
    startingY: number,
    endingX: number,
    endingY: number,
    opts: PathfinderOptions,
    obstacles: PathfinderObstacle[] = [],
    side?: Side,
    destNode?: PathfinderObstacle,
    startSide?: Side,
    startNode?: PathfinderObstacle,
): Segment[] {
    return orthogonalRoute(
        startingX,
        startingY,
        endingX,
        endingY,
        opts,
        obstacles,
        side,
        destNode,
        startSide,
        startNode,
    ).segments;
}
