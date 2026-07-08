import {
    ConnectionBehaviorSummary,
    ConnectionModel,
    ConnectionPropertyMappingModel,
    resolveEffectivePropertyMappings,
} from './connection-model';
import { Point, Side } from './node-model';
import { PathfinderOptions } from './pathfinder';
import {
    CanvasBounds,
    CanvasConnectionLane,
    CanvasConnectionPresentation,
    CanvasConnectionShade,
    CanvasDetailTier,
    CanvasRouteValidationError,
    CanvasValidationSeverity,
    RenderedConnectionRoute,
} from './canvas-render-types';
import { drawRenderedConnection } from './canvas-connection-drawing-utility';
import { buildConnectionVisualRoutes } from './canvas-render-connection-routing';
import {
    boundsContainPoint,
    distanceToSegment,
    unionRouteBounds,
} from './canvas-render-geometry-utilities';
import { RenderedNode } from './canvas-render-node-model';

export class RenderedConnection {
    readonly id: string;
    readonly anchor: number;
    readonly side: Side;
    readonly pos: number;
    readonly propertyMappings: readonly ConnectionPropertyMappingModel[];
    readonly effectiveMappings: readonly ConnectionPropertyMappingModel[];
    readonly routes: readonly RenderedConnectionRoute[];
    readonly routeValidationErrors: readonly CanvasRouteValidationError[];
    readonly routeBounds: CanvasBounds | null;
    readonly badgePosition: Point | null;
    readonly behavior: ConnectionBehaviorSummary | null;
    readonly showBehaviorCue: boolean;

    constructor(
        public readonly base: ConnectionModel,
        public readonly from: RenderedNode,
        public readonly to: RenderedNode,
        detailTier: CanvasDetailTier,
        pathfinderOptions: PathfinderOptions,
        obstacles: RenderedNode[],
        behavior: ConnectionBehaviorSummary | null,
        showBehaviorCue: boolean,
        lane: CanvasConnectionLane,
        shade: CanvasConnectionShade,
        precomputedRoutes: readonly RenderedConnectionRoute[] | null = null,
    ) {
        this.id = base.id;
        this.anchor = base.anchor;
        this.side = base.side;
        this.pos = base.pos;
        this.propertyMappings = base.propertyMappings;
        this.behavior = behavior;
        this.showBehaviorCue = showBehaviorCue && detailTier === 'full';
        this.effectiveMappings = resolveEffectivePropertyMappings(base);
        this.routes =
            precomputedRoutes ??
            buildConnectionVisualRoutes(
                this.from,
                this.to,
                this.anchor,
                this.effectiveMappings,
                detailTier,
                pathfinderOptions,
                obstacles,
                lane,
                shade,
            );
        this.routeValidationErrors = this.routes.flatMap((route, routeIndex) =>
            route.validationMessage
                ? [
                      {
                          connectionId: this.id,
                          routeIndex,
                          message: route.validationMessage,
                      },
                  ]
                : [],
        );
        this.routeBounds = unionRouteBounds(this.routes);
        this.badgePosition = null;
    }

    draw(
        wrapper: { ctx: CanvasRenderingContext2D },
        options?: {
            selected?: boolean;
            hovered?: boolean;
            dimmed?: boolean;
            presentation?: CanvasConnectionPresentation;
            hoveredRouteIndex?: number | null;
            validationSeverity?: CanvasValidationSeverity | null;
        },
    ): void {
        drawRenderedConnection(this, wrapper, options);
    }

    hitTest(x: number, y: number, tolerance: number): boolean {
        return this.distanceToVisualRoute(x, y, tolerance) <= tolerance;
    }

    distanceToVisualRoute(x: number, y: number, boundsPadding = 0): number {
        return (
            this.nearestVisualRoute(x, y, boundsPadding)?.distance ??
            Number.POSITIVE_INFINITY
        );
    }

    nearestVisualRoute(
        x: number,
        y: number,
        boundsPadding = 0,
    ): { routeIndex: number; distance: number } | null {
        if (
            this.routeBounds &&
            !boundsContainPoint(this.routeBounds, x, y, boundsPadding)
        ) {
            return null;
        }

        let nearestDistance = Number.POSITIVE_INFINITY;
        let nearestRouteIndex = -1;
        for (const [routeIndex, route] of this.routes.entries()) {
            for (const segment of route.visualSegments) {
                const distance = distanceToSegment(
                    x,
                    y,
                    segment.x1,
                    segment.y1,
                    segment.x2,
                    segment.y2,
                );
                if (distance < nearestDistance) {
                    nearestDistance = distance;
                    nearestRouteIndex = routeIndex;
                }
            }
        }

        return nearestRouteIndex >= 0
            ? { routeIndex: nearestRouteIndex, distance: nearestDistance }
            : null;
    }

    badgeHitTest(x: number, y: number, tolerance: number): boolean {
        return (
            !!this.badgePosition &&
            Math.hypot(x - this.badgePosition.x, y - this.badgePosition.y) <=
                tolerance
        );
    }
}
