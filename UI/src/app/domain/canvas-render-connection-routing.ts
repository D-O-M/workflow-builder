import { ConnectionPropertyMappingModel } from './connection-model';
import { NodePortDefinition } from './node-model';
import { PathfinderOptions, orthogonalRoute } from './pathfinder';
import {
  CanvasConnectionLane,
  CanvasConnectionShade,
  CanvasDetailTier,
  RenderedConnectionRoute,
} from './canvas-render-types';
import { RenderedNode } from './canvas-render-node-model';
import {
  createLaneAdjustedProblemSegments,
  createLaneAdjustedSegments,
  unionSegmentBounds,
} from './canvas-render-geometry-utilities';
import { offsetConnectionShade } from './canvas-render-lane-shade';

export function buildConnectionVisualRoutes(
  from: RenderedNode,
  to: RenderedNode,
  anchor: number,
  effectiveMappings: readonly ConnectionPropertyMappingModel[],
  detailTier: CanvasDetailTier,
  pathfinderOptions: PathfinderOptions,
  obstacles: RenderedNode[],
  lane: CanvasConnectionLane,
  shade: CanvasConnectionShade,
): RenderedConnectionRoute[] {
  const sourceSummary = from.outputSummaryPort(anchor);
  const targetSummary = to.inputSummaryPort();
  if (!sourceSummary || !targetSummary) {
    return [];
  }

  const sourceHasExpandedOutput = from.sectionForPort(sourceSummary)?.expanded === true;
  const targetHasExpandedInput = to.sectionForPort(targetSummary)?.expanded === true;

  if (detailTier === 'simplified' || (!sourceHasExpandedOutput && !targetHasExpandedInput) || effectiveMappings.length === 0) {
    return [buildConnectionRoute(sourceSummary, targetSummary, from, to, pathfinderOptions, obstacles, lane, shade, null)];
  }

  return effectiveMappings.map((mapping, routeIndex) => {
    const startPort = sourceHasExpandedOutput
      ? from.outputPropertyPort(anchor, mapping.sourceProperty) ?? sourceSummary
      : sourceSummary;
    const endPort = targetHasExpandedInput
      ? to.inputPropertyPort(mapping.targetProperty) ?? targetSummary
      : targetSummary;

    return buildConnectionRoute(
      startPort,
      endPort,
      from,
      to,
      pathfinderOptions,
      obstacles,
      lane,
      offsetConnectionShade(shade, routeIndex),
      mapping,
    );
  });
}

function buildConnectionRoute(
  start: NodePortDefinition,
  end: NodePortDefinition,
  from: RenderedNode,
  to: RenderedNode,
  options: PathfinderOptions,
  obstacles: RenderedNode[],
  lane: CanvasConnectionLane,
  shade: CanvasConnectionShade,
  mapping: ConnectionPropertyMappingModel | null,
): RenderedConnectionRoute {
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
    to,
    start.side,
    from,
  );

  const visualSegments = lane.offset === 0
    ? route.segments
    : createLaneAdjustedSegments(route.segments, lane.offset);
  const visualProblemSegments = lane.offset === 0 || visualSegments === route.segments
    ? route.problemSegments
    : createLaneAdjustedProblemSegments(route.problemSegments, route.segments, lane.offset);

  return {
    segments: route.segments,
    visualSegments,
    status: route.status,
    problemSegments: route.problemSegments,
    visualProblemSegments,
    validationMessage: route.validationMessage,
    tip: { x: tipX, y: tipY },
    startSide: start.side,
    side: end.side,
    bounds: unionSegmentBounds(visualSegments),
    canonicalBounds: unionSegmentBounds(route.segments),
    lane,
    shade,
    startPortKey: start.key,
    endPortKey: end.key,
    sourceProperty: mapping?.sourceProperty ?? null,
    targetProperty: mapping?.targetProperty ?? null,
  };
}
