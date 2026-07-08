import { ConnectionPropertyMappingModel } from './connection-model';
import { PathfinderOptions, Segment } from './pathfinder';
import {
    CanvasConnectionLane,
    CanvasConnectionShade,
    CanvasDetailTier,
    RenderedConnectionRoute,
} from './canvas-render-types';
import { RenderedNode } from './canvas-render-node-model';

export interface CanvasRouteCacheLookup {
    readonly connectionId: string;
    readonly anchor: number;
    readonly side: string;
    readonly pos: number;
    readonly from: RenderedNode;
    readonly to: RenderedNode;
    readonly detailTier: CanvasDetailTier;
    readonly pathfinderOptions: PathfinderOptions;
    readonly effectiveMappings: readonly ConnectionPropertyMappingModel[];
    readonly lane: CanvasConnectionLane;
    readonly shade: CanvasConnectionShade;
    readonly obstacleSignature: string;
}

export interface CanvasRouteCacheStats {
    readonly hits: number;
    readonly misses: number;
    readonly sets: number;
}

export class CanvasConnectionRouteCache {
    private readonly entries = new Map<
        string,
        readonly RenderedConnectionRoute[]
    >();
    private hitCount = 0;
    private missCount = 0;
    private setCount = 0;

    constructor(private readonly maxEntries = 1200) {}

    get(
        lookup: CanvasRouteCacheLookup,
    ): readonly RenderedConnectionRoute[] | null {
        const key = canvasRouteCacheKey(lookup);
        const existing = this.entries.get(key);
        if (!existing) {
            this.missCount += 1;
            return null;
        }

        this.entries.delete(key);
        this.entries.set(key, existing);
        this.hitCount += 1;
        return existing;
    }

    set(
        lookup: CanvasRouteCacheLookup,
        routes: readonly RenderedConnectionRoute[],
    ): void {
        const key = canvasRouteCacheKey(lookup);
        this.entries.delete(key);
        this.entries.set(key, routes);
        this.setCount += 1;

        while (this.entries.size > this.maxEntries) {
            const oldestKey = this.entries.keys().next().value as
                | string
                | undefined;
            if (!oldestKey) {
                break;
            }

            this.entries.delete(oldestKey);
        }
    }

    clear(): void {
        this.entries.clear();
        this.hitCount = 0;
        this.missCount = 0;
        this.setCount = 0;
    }

    stats(): CanvasRouteCacheStats {
        return {
            hits: this.hitCount,
            misses: this.missCount,
            sets: this.setCount,
        };
    }
}

export function canvasRouteCacheKey(lookup: CanvasRouteCacheLookup): string {
    return [
        lookup.connectionId,
        lookup.anchor,
        lookup.side,
        roundForKey(lookup.pos),
        lookup.detailTier,
        pathfinderOptionsSignature(lookup.pathfinderOptions),
        nodeRouteSignature(lookup.from),
        nodeRouteSignature(lookup.to),
        mappingsSignature(lookup.effectiveMappings),
        laneSignature(lookup.lane),
        shadeSignature(lookup.shade),
        lookup.obstacleSignature,
    ].join('|');
}

export function nodeRouteSignature(node: RenderedNode): string {
    const ports = node
        .visiblePorts()
        .map((port) =>
            [
                port.key,
                port.side,
                roundForKey(port.x),
                roundForKey(port.y),
                port.kind,
                port.anchorIndex ?? '',
                port.channelName ?? '',
                port.propertyName ?? '',
            ].join(':'),
        )
        .join(',');

    return [
        node.id,
        roundForKey(node.x),
        roundForKey(node.y),
        roundForKey(node.width),
        roundForKey(node.height),
        node.anySectionExpanded ? 1 : 0,
        node.allSectionsExpanded ? 1 : 0,
        ports,
    ].join(';');
}

export function obstacleSignature(obstacles: readonly RenderedNode[]): string {
    return obstacles
        .map((node) =>
            [
                node.id,
                roundForKey(node.x),
                roundForKey(node.y),
                roundForKey(node.width),
                roundForKey(node.height),
            ].join(':'),
        )
        .sort()
        .join(',');
}

export function routeSegmentsSignature(segments: readonly Segment[]): string {
    return segments
        .map((segment) =>
            [
                roundForKey(segment.x1),
                roundForKey(segment.y1),
                roundForKey(segment.x2),
                roundForKey(segment.y2),
            ].join(':'),
        )
        .join(',');
}

function pathfinderOptionsSignature(options: PathfinderOptions): string {
    return [
        roundForKey(options.gridSize),
        roundForKey(options.nodeMargin),
        roundForKey(options.obstacleBuffer ?? 0),
    ].join(':');
}

function mappingsSignature(
    mappings: readonly ConnectionPropertyMappingModel[],
): string {
    return mappings
        .map(
            (mapping) => `${mapping.sourceProperty}->${mapping.targetProperty}`,
        )
        .join(',');
}

function laneSignature(lane: CanvasConnectionLane): string {
    return [roundForKey(lane.offset), lane.index, lane.groupSize].join(':');
}

function shadeSignature(shade: CanvasConnectionShade): string {
    return [
        shade.index,
        shade.total,
        shade.sourceIndex,
        shade.sourceTotal,
        shade.variantIndex,
        shade.variantTotal,
        shade.semanticStatus ?? '',
    ].join(':');
}

function roundForKey(value: number): string {
    return (Math.round(value * 1000) / 1000).toString();
}
