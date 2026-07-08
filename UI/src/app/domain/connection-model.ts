import { NodeModel, Point, Side } from './node-model';
import { CanvasWrapper } from './canvas-wrapper';
import {
    PathfinderOptions,
    RouteProblemSegment,
    RouteStatus,
    orthogonalRoute,
    subtractRouteProblemSegments,
} from './pathfinder';

export interface ConnectionPropertyMappingModel {
    sourceProperty: string;
    targetProperty: string;
}

export type ConnectionBehaviorKind =
    | 'plain'
    | 'mapped'
    | 'fanOut'
    | 'mappedFanOut';

export interface ConnectionBehaviorSummary {
    kind: ConnectionBehaviorKind;
    isFanOut: boolean;
    isMappedOrPartial: boolean;
    siblingCount: number;
    mappedPropertyCount: number;
    directPropertyCount: number;
    remappedPropertyCount: number;
    targetPropertyCount: number;
    unmappedTargetCount: number;
    behaviorLabel: string;
    summaryLabel: string;
    showBadge: boolean;
}

interface ConnectionVisualRoute {
    segments: { x1: number; y1: number; x2: number; y2: number }[];
    status: RouteStatus;
    problemSegments: RouteProblemSegment[];
    tip: Point;
    startSide: Side;
    side: Side;
}

const LEGACY_CONNECTION_STROKE_WIDTH = 7.8;
const LEGACY_CONNECTION_FOCUSED_STROKE_WIDTH = 12;

export function describeConnectionBehavior(
    connection: ConnectionModel,
    allConnections: readonly ConnectionModel[],
): ConnectionBehaviorSummary {
    const siblingCount = allConnections.filter(
        (candidate) =>
            candidate.from.id === connection.from.id &&
            candidate.anchor === connection.anchor,
    ).length;
    const isFanOut = siblingCount > 1;

    const sourceProperties = connection.from.outputPropertiesForAnchor(
        connection.anchor,
    );
    const targetProperties = Object.keys(connection.to.inputProperties).sort(
        (left, right) => left.localeCompare(right),
    );
    const directTargetProperties = targetProperties.filter((targetProperty) =>
        Object.prototype.hasOwnProperty.call(sourceProperties, targetProperty),
    );
    const mappedPropertyCount = connection.propertyMappings.length;
    const directPropertyCount = connection.propertyMappings.filter(
        (mapping) => mapping.sourceProperty === mapping.targetProperty,
    ).length;
    const remappedPropertyCount = mappedPropertyCount - directPropertyCount;
    const explicitMappingsByTarget = new Map(
        connection.propertyMappings.map(
            (mapping) =>
                [mapping.targetProperty, mapping.sourceProperty] as const,
        ),
    );
    const isPlainFullTransfer =
        mappedPropertyCount === directTargetProperties.length &&
        directTargetProperties.every(
            (targetProperty) =>
                explicitMappingsByTarget.get(targetProperty) === targetProperty,
        ) &&
        connection.propertyMappings.every(
            (mapping) => mapping.sourceProperty === mapping.targetProperty,
        );
    const isMappedOrPartial = !isPlainFullTransfer;
    const kind: ConnectionBehaviorKind = isMappedOrPartial
        ? isFanOut
            ? 'mappedFanOut'
            : 'mapped'
        : isFanOut
          ? 'fanOut'
          : 'plain';
    const unmappedTargetCount = Math.max(
        targetProperties.length - mappedPropertyCount,
        0,
    );
    const summaryLabel = buildBehaviorSummaryLabel(
        kind,
        siblingCount,
        mappedPropertyCount,
        unmappedTargetCount,
    );

    return {
        kind,
        isFanOut,
        isMappedOrPartial,
        siblingCount,
        mappedPropertyCount,
        directPropertyCount,
        remappedPropertyCount,
        targetPropertyCount: targetProperties.length,
        unmappedTargetCount,
        behaviorLabel: behaviorLabelForKind(kind),
        summaryLabel,
        showBadge: kind !== 'plain',
    };
}

export function buildConnectionMappingSelections(
    connection: ConnectionModel,
): Record<string, string> {
    const sourceProperties = connection.from.outputPropertiesForAnchor(
        connection.anchor,
    );
    const explicitMappings = new Map(
        connection.propertyMappings.map(
            (mapping) =>
                [mapping.targetProperty, mapping.sourceProperty] as const,
        ),
    );
    const selections: Record<string, string> = {};

    for (const targetProperty of Object.keys(
        connection.to.inputProperties,
    ).sort((left, right) => left.localeCompare(right))) {
        selections[targetProperty] =
            explicitMappings.get(targetProperty) ??
            (Object.prototype.hasOwnProperty.call(
                sourceProperties,
                targetProperty,
            )
                ? targetProperty
                : '');
    }

    return selections;
}

export function buildConnectionMappingsFromSelections(
    selections: Record<string, string>,
): ConnectionPropertyMappingModel[] {
    return Object.entries(selections)
        .filter(([, sourceProperty]) => sourceProperty.trim().length > 0)
        .map(([targetProperty, sourceProperty]) => ({
            sourceProperty,
            targetProperty,
        }))
        .sort((left, right) =>
            left.targetProperty.localeCompare(right.targetProperty),
        );
}

export function resolveEffectivePropertyMappings(
    connection: ConnectionModel,
): ConnectionPropertyMappingModel[] {
    return buildConnectionMappingsFromSelections(
        buildConnectionMappingSelections(connection),
    );
}

function behaviorLabelForKind(kind: ConnectionBehaviorKind): string {
    switch (kind) {
        case 'mapped':
            return 'Mapped transfer';
        case 'fanOut':
            return 'Fan-out';
        case 'mappedFanOut':
            return 'Mapped fan-out';
        default:
            return 'Full transfer';
    }
}

function buildBehaviorSummaryLabel(
    kind: ConnectionBehaviorKind,
    siblingCount: number,
    mappedPropertyCount: number,
    unmappedTargetCount: number,
): string {
    switch (kind) {
        case 'mapped':
            return unmappedTargetCount > 0
                ? `${mappedPropertyCount} properties forwarded, ${unmappedTargetCount} left unmapped`
                : `${mappedPropertyCount} properties forwarded with explicit mapping`;
        case 'fanOut':
            return `Full transfer to ${siblingCount} downstream nodes`;
        case 'mappedFanOut':
            return `${mappedPropertyCount} mapped properties fan out to ${siblingCount} downstream nodes`;
        default:
            return 'All matching same-name properties flow directly';
    }
}

export class ConnectionModel {
    private static readonly themeByStatus = {
        default: {
            connection: '#2d4bdd',
            connectionSoft: 'rgba(45, 75, 221, 0.78)',
        },
        Completed: {
            connection: '#12b76a',
            connectionSoft: 'rgba(18, 183, 106, 0.72)',
        },
        InProgress: {
            connection: '#2d4bdd',
            connectionSoft: 'rgba(45, 75, 221, 0.78)',
        },
        Failed: {
            connection: '#d92d20',
            connectionSoft: 'rgba(217, 45, 32, 0.72)',
        },
        Pending: {
            connection: '#64748b',
            connectionSoft: 'rgba(100, 116, 139, 0.72)',
        },
        NotExecuted: {
            connection: '#94a3b8',
            connectionSoft: 'rgba(148, 163, 184, 0.68)',
        },
    } as const;

    constructor(
        public from: NodeModel,
        public to: NodeModel,
        public anchor: number,
        public side: Side,
        public pos: number,
        public id: string = crypto.randomUUID(),
        public propertyMappings: ConnectionPropertyMappingModel[] = [],
    ) {}

    segments(
        options: PathfinderOptions,
        obstacles: NodeModel[] = [],
    ): { x1: number; y1: number; x2: number; y2: number }[] {
        return this.routeSegments(options, obstacles)[0]?.segments ?? [];
    }

    badgePosition(
        options: PathfinderOptions,
        obstacles: NodeModel[] = [],
    ): Point | null {
        return null;
    }

    draw(
        wrapper: CanvasWrapper,
        obstacles: NodeModel[] = [],
        options?: {
            selected?: boolean;
            behavior?: ConnectionBehaviorSummary;
            showBehaviorCue?: boolean;
        },
    ): void {
        const theme = this.from.renderStatus
            ? ConnectionModel.themeByStatus[this.from.renderStatus]
            : ConnectionModel.themeByStatus.default;
        const selected = options?.selected ?? false;
        const routes = this.routeSegments(
            wrapper.pathfinderOptions(),
            obstacles,
        );

        const ctx = wrapper.ctx as CanvasRenderingContext2D;
        ctx.save();
        const hasProblemRoutes = routes.some(
            (route) => route.status === 'obstacleViolation',
        );
        const normalStroke = selected ? '#2563eb' : theme.connectionSoft;
        const problemStroke = '#d92d20';
        const problemSoft = 'rgba(217, 45, 32, 0.82)';

        ctx.strokeStyle = hasProblemRoutes ? problemSoft : normalStroke;
        ctx.fillStyle = hasProblemRoutes ? problemStroke : normalStroke;
        ctx.lineWidth = selected
            ? LEGACY_CONNECTION_FOCUSED_STROKE_WIDTH
            : LEGACY_CONNECTION_STROKE_WIDTH;
        ctx.lineCap = 'butt';
        ctx.lineJoin = 'round';

        for (const route of routes) {
            if (route.segments.length === 0) {
                continue;
            }

            const lineWidth = selected
                ? LEGACY_CONNECTION_FOCUSED_STROKE_WIDTH
                : LEGACY_CONNECTION_STROKE_WIDTH;
            const routeStroke =
                route.status === 'obstacleViolation'
                    ? problemSoft
                    : normalStroke;
            ctx.lineWidth = lineWidth;
            ctx.strokeStyle = routeStroke;
            ctx.fillStyle = routeStroke;

            if (route.problemSegments.length > 0) {
                strokeSegments(
                    ctx,
                    subtractRouteProblemSegments(
                        route.segments,
                        route.problemSegments,
                    ),
                );
                ctx.save();
                ctx.strokeStyle = problemStroke;
                ctx.lineWidth = selected ? 13.5 : 9;
                ctx.setLineDash([8, 6]);
                strokeSegments(ctx, route.problemSegments);
                ctx.restore();
            } else {
                strokeContinuousRoute(ctx, route.segments);
            }

            fillRouteEndpointCaps(ctx, route, lineWidth);
        }

        ctx.restore();
    }

    hitTest(
        x: number,
        y: number,
        tolerance: number,
        options: PathfinderOptions,
        obstacles: NodeModel[] = [],
    ): boolean {
        return this.routeSegments(options, obstacles).some((route) =>
            route.segments.some(
                (segment) =>
                    distanceToSegment(
                        x,
                        y,
                        segment.x1,
                        segment.y1,
                        segment.x2,
                        segment.y2,
                    ) <= tolerance,
            ),
        );
    }

    badgeHitTest(
        x: number,
        y: number,
        tolerance: number,
        options: PathfinderOptions,
        obstacles: NodeModel[] = [],
    ): boolean {
        const badge = this.badgePosition(options, obstacles);
        return !!badge && Math.hypot(x - badge.x, y - badge.y) <= tolerance;
    }

    private routeSegments(
        options: PathfinderOptions,
        obstacles: NodeModel[] = [],
    ): ConnectionVisualRoute[] {
        const sourceSummary = this.from.outputSummaryPort(this.anchor);
        const targetSummary = this.to.inputSummaryPort();
        if (!sourceSummary || !targetSummary) {
            return [];
        }

        const effectiveMappings = resolveEffectivePropertyMappings(this);
        const sourceExpanded = this.from.expanded;
        const targetExpanded = this.to.expanded;

        if (
            (!sourceExpanded && !targetExpanded) ||
            effectiveMappings.length === 0
        ) {
            return [
                this.buildRoute(
                    sourceSummary,
                    targetSummary,
                    options,
                    obstacles,
                ),
            ];
        }

        return effectiveMappings.map((mapping) => {
            const startPort = sourceExpanded
                ? (this.from.outputPropertyPort(
                      this.anchor,
                      mapping.sourceProperty,
                  ) ?? sourceSummary)
                : sourceSummary;
            const endPort = targetExpanded
                ? (this.to.inputPropertyPort(mapping.targetProperty) ??
                  targetSummary)
                : targetSummary;
            return this.buildRoute(startPort, endPort, options, obstacles);
        });
    }

    private buildRoute(
        start: { x: number; y: number; side: Side },
        end: { x: number; y: number; side: Side },
        options: PathfinderOptions,
        obstacles: NodeModel[],
    ): ConnectionVisualRoute {
        const tipX = end.x;
        const tipY = end.y;
        const route = orthogonalRoute(
            start.x,
            start.y,
            tipX,
            tipY,
            options,
            obstacles,
            end.side,
            this.to,
            start.side,
            this.from,
        );

        return {
            segments: route.segments,
            status: route.status,
            problemSegments: [...route.problemSegments],
            tip: { x: tipX, y: tipY },
            startSide: start.side,
            side: end.side,
        };
    }
}

function strokeContinuousRoute(
    ctx: CanvasRenderingContext2D,
    segments: readonly { x1: number; y1: number; x2: number; y2: number }[],
): void {
    if (segments.length === 0) {
        return;
    }

    const points = [
        { x: segments[0]!.x1, y: segments[0]!.y1 },
        ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
    ];
    if (points.length < 3) {
        strokeSegments(ctx, segments);
        return;
    }

    const cornerRadius = 7;
    ctx.beginPath();
    ctx.moveTo(points[0]!.x, points[0]!.y);

    for (let index = 1; index < points.length - 1; index += 1) {
        const previous = points[index - 1]!;
        const current = points[index]!;
        const next = points[index + 1]!;

        if (
            (previous.x === current.x && current.x === next.x) ||
            (previous.y === current.y && current.y === next.y)
        ) {
            ctx.lineTo(current.x, current.y);
            continue;
        }

        const previousDistance =
            Math.abs(current.x - previous.x) + Math.abs(current.y - previous.y);
        const nextDistance =
            Math.abs(next.x - current.x) + Math.abs(next.y - current.y);
        const radius = Math.min(
            cornerRadius,
            previousDistance / 2,
            nextDistance / 2,
        );
        if (radius <= 0) {
            ctx.lineTo(current.x, current.y);
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
        ctx.lineTo(cornerStart.x, cornerStart.y);
        ctx.quadraticCurveTo(current.x, current.y, cornerEnd.x, cornerEnd.y);
    }

    const lastPoint = points[points.length - 1]!;
    ctx.lineTo(lastPoint.x, lastPoint.y);
    ctx.stroke();
}

function strokeSegments(
    ctx: CanvasRenderingContext2D,
    segments: readonly { x1: number; y1: number; x2: number; y2: number }[],
): void {
    for (const segment of segments) {
        ctx.beginPath();
        ctx.moveTo(segment.x1, segment.y1);
        ctx.lineTo(segment.x2, segment.y2);
        ctx.stroke();
    }
}

function fillRouteEndpointCaps(
    ctx: CanvasRenderingContext2D,
    route: ConnectionVisualRoute,
    lineWidth: number,
): void {
    const firstSegment = route.segments[0];
    const lastSegment = route.segments[route.segments.length - 1];
    if (!firstSegment || !lastSegment) {
        return;
    }

    fillConnectionEndpointCap(
        ctx,
        { x: firstSegment.x1, y: firstSegment.y1 },
        route.startSide,
        lineWidth,
    );
    fillConnectionEndpointCap(
        ctx,
        { x: lastSegment.x2, y: lastSegment.y2 },
        route.side,
        lineWidth,
    );
}

function fillConnectionEndpointCap(
    ctx: CanvasRenderingContext2D,
    point: Point,
    side: Side,
    lineWidth: number,
): void {
    const inward = inwardVectorForSide(side);
    const perpendicular = { x: -inward.y, y: inward.x };
    const length = Math.max(8, lineWidth * 1.25);
    const halfWidth = Math.max(4, lineWidth * 0.68);
    const tip = {
        x: point.x + inward.x * length,
        y: point.y + inward.y * length,
    };

    ctx.beginPath();
    ctx.moveTo(tip.x, tip.y);
    ctx.lineTo(
        point.x + perpendicular.x * halfWidth,
        point.y + perpendicular.y * halfWidth,
    );
    ctx.lineTo(
        point.x - perpendicular.x * halfWidth,
        point.y - perpendicular.y * halfWidth,
    );
    ctx.closePath();
    ctx.fill();
}

function inwardVectorForSide(side: Side): Point {
    switch (side) {
        case 'left':
            return { x: 1, y: 0 };
        case 'right':
            return { x: -1, y: 0 };
        case 'top':
            return { x: 0, y: 1 };
        case 'bottom':
            return { x: 0, y: -1 };
    }
}

function distanceToSegment(
    px: number,
    py: number,
    x1: number,
    y1: number,
    x2: number,
    y2: number,
): number {
    const dx = x2 - x1;
    const dy = y2 - y1;

    if (dx === 0 && dy === 0) {
        return Math.hypot(px - x1, py - y1);
    }

    const numerator = (px - x1) * dx + (py - y1) * dy;
    const denominator = dx * dx + dy * dy;
    const t = Math.min(1, Math.max(0, numerator / denominator));
    const closestX = x1 + dx * t;
    const closestY = y1 + dy * t;
    return Math.hypot(px - closestX, py - closestY);
}
