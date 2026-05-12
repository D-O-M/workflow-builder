import { ConnectionModel, ConnectionPropertyMappingModel, resolveEffectivePropertyMappings } from './connection-model';
import { NodeRenderStatus } from './node-model';
import {
  CanvasConnectionLane,
  CanvasConnectionSemanticStatus,
  CanvasConnectionShade,
  CanvasDetailTier,
  CanvasValidationSeverity,
  LANE_CENTER_SPACING,
} from './canvas-render-types';
import { RenderedNode } from './canvas-render-node-model';

export const DEFAULT_CONNECTION_LANE: CanvasConnectionLane = {
  offset: 0,
  index: 0,
  groupSize: 1,
};

export const DEFAULT_CONNECTION_SHADE: CanvasConnectionShade = {
  index: 0,
  total: 1,
  sourceIndex: 0,
  sourceTotal: 1,
  variantIndex: 0,
  variantTotal: 1,
};

interface LaneGroupPosition {
  readonly offset: number;
  readonly index: number;
  readonly groupSize: number;
}


export function buildConnectionLaneAssignments(connections: readonly ConnectionModel[]): ReadonlyMap<string, CanvasConnectionLane> {
  const mutable = new Map<string, { source?: LaneGroupPosition; target?: LaneGroupPosition }>();

  applyLaneGroupPositions(
    mutable,
    connections,
    (connection) => `${connection.from.id}:${connection.anchor}`,
    'source',
  );
  applyLaneGroupPositions(
    mutable,
    connections,
    (connection) => `${connection.to.id}:input`,
    'target',
  );

  return new Map(
    connections.map((connection) => {
      const entry = mutable.get(connection.id);
      if (!entry?.source && !entry?.target) {
        return [connection.id, DEFAULT_CONNECTION_LANE] as const;
      }

      const dominant = chooseDominantLaneGroup(entry.source, entry.target);
      return [
        connection.id,
        {
          offset: combineLaneOffsets(entry.source?.offset ?? 0, entry.target?.offset ?? 0),
          index: dominant?.index ?? 0,
          groupSize: dominant?.groupSize ?? 1,
        },
      ] as const;
    }),
  );
}

export function buildConnectionShadeAssignments(
  connections: readonly ConnectionModel[],
  nodeById: ReadonlyMap<number, RenderedNode>,
  detailTier: CanvasDetailTier,
): ReadonlyMap<string, CanvasConnectionShade> {
  const routeCounts = new Map<string, number>();
  const connectionsBySourceId = new Map<number, ConnectionModel[]>();
  let totalRoutes = 0;

  for (const connection of connections) {
    const from = nodeById.get(connection.from.id);
    const to = nodeById.get(connection.to.id);
    if (!from || !to) {
      continue;
    }

    const routeCount = visibleRouteCount(from, to, connection.anchor, resolveEffectivePropertyMappings(connection), detailTier);
    routeCounts.set(connection.id, routeCount);
    totalRoutes += routeCount;

    const sourceConnections = connectionsBySourceId.get(from.id);
    if (sourceConnections) {
      sourceConnections.push(connection);
    } else {
      connectionsBySourceId.set(from.id, [connection]);
    }
  }

  const assignments = new Map<string, CanvasConnectionShade>();
  const sourceIds = [...connectionsBySourceId.keys()].sort((left, right) => left - right);
  let routeCursor = 0;
  sourceIds.forEach((sourceId, sourceIndex) => {
    const sourceConnections = connectionsBySourceId.get(sourceId)!;
    const sourceRouteTotal = sourceConnections.reduce((total, connection) => total + (routeCounts.get(connection.id) ?? 1), 0);
    let variantCursor = 0;

    for (const connection of [...sourceConnections].sort(compareConnectionsForLanes)) {
      const routeCount = routeCounts.get(connection.id) ?? 1;
      assignments.set(connection.id, {
        index: routeCursor,
        total: Math.max(totalRoutes, 1),
        sourceIndex,
        sourceTotal: Math.max(sourceIds.length, 1),
        variantIndex: variantCursor,
        variantTotal: Math.max(sourceRouteTotal, 1),
      });
      routeCursor += routeCount;
      variantCursor += routeCount;
    }
  });

  return assignments;
}

function visibleRouteCount(
  from: RenderedNode,
  to: RenderedNode,
  anchor: number,
  effectiveMappings: readonly ConnectionPropertyMappingModel[],
  detailTier: CanvasDetailTier,
): number {
  const sourceSummary = from.outputSummaryPort(anchor);
  const targetSummary = to.inputSummaryPort();
  if (!sourceSummary || !targetSummary) {
    return 0;
  }

  const sourceHasExpandedOutput = from.sectionForPort(sourceSummary)?.expanded === true;
  const targetHasExpandedInput = to.sectionForPort(targetSummary)?.expanded === true;
  return detailTier === 'simplified' || (!sourceHasExpandedOutput && !targetHasExpandedInput) || effectiveMappings.length === 0
    ? 1
    : effectiveMappings.length;
}

export function offsetConnectionShade(shade: CanvasConnectionShade, routeIndex: number): CanvasConnectionShade {
  return {
    ...shade,
    index: shade.index + routeIndex,
    variantIndex: shade.variantIndex + routeIndex,
  };
}

export function connectionShadeForMode(
  shade: CanvasConnectionShade,
  sourceStatus: NodeRenderStatus | null,
  readOnly: boolean,
): CanvasConnectionShade {
  if (!readOnly) {
    return shade;
  }

  return {
    ...shade,
    semanticStatus: connectionSemanticStatusForSource(sourceStatus),
  };
}

function connectionSemanticStatusForSource(sourceStatus: NodeRenderStatus | null): CanvasConnectionSemanticStatus {
  if (sourceStatus === 'Completed') {
    return 'completed';
  }

  if (sourceStatus === 'Failed') {
    return 'failed';
  }

  return 'inactive';
}

function applyLaneGroupPositions(
  laneByConnectionId: Map<string, { source?: LaneGroupPosition; target?: LaneGroupPosition }>,
  connections: readonly ConnectionModel[],
  keySelector: (connection: ConnectionModel) => string,
  laneKind: 'source' | 'target',
): void {
  const groups = new Map<string, ConnectionModel[]>();
  for (const connection of connections) {
    const key = keySelector(connection);
    const existing = groups.get(key);
    if (existing) {
      existing.push(connection);
    } else {
      groups.set(key, [connection]);
    }
  }

  for (const group of groups.values()) {
    if (group.length <= 1) {
      continue;
    }

    const ordered = [...group].sort(compareConnectionsForLanes);
    ordered.forEach((connection, index) => {
      const current = laneByConnectionId.get(connection.id) ?? {};
      current[laneKind] = {
        offset: laneOffsetForIndex(index, ordered.length),
        index,
        groupSize: ordered.length,
      };
      laneByConnectionId.set(connection.id, current);
    });
  }
}

function compareConnectionsForLanes(left: ConnectionModel, right: ConnectionModel): number {
  return (
    left.from.id - right.from.id ||
    left.to.id - right.to.id ||
    left.anchor - right.anchor ||
    left.pos - right.pos ||
    left.id.localeCompare(right.id)
  );
}

function laneOffsetForIndex(index: number, count: number): number {
  if (count <= 1) {
    return 0;
  }

  return (index - (count - 1) / 2) * LANE_CENTER_SPACING;
}

function chooseDominantLaneGroup(source?: LaneGroupPosition, target?: LaneGroupPosition): LaneGroupPosition | null {
  if (!source) {
    return target ?? null;
  }

  if (!target) {
    return source;
  }

  return target.groupSize > source.groupSize ? target : source;
}

function combineLaneOffsets(sourceOffset: number, targetOffset: number): number {
  if (sourceOffset === 0) {
    return targetOffset;
  }

  if (targetOffset === 0) {
    return sourceOffset;
  }

  const combined = (sourceOffset + targetOffset) / 2;
  return clampLaneOffset(combined === 0 ? sourceOffset : combined);
}

function clampLaneOffset(offset: number): number {
  return Math.max(-18, Math.min(18, offset));
}

export function connectionShadeStroke(shade: CanvasConnectionShade, focused: boolean): string {
  return connectionShadeColor(shade, focused, false);
}

export function connectionShadeFill(shade: CanvasConnectionShade, focused: boolean): string {
  return connectionShadeColor(shade, focused, true);
}

export function validationConnectionColor(severity: CanvasValidationSeverity, strong: boolean): string {
  if (severity === 'error') {
    return strong ? '#d92d20' : '#f04438';
  }

  return strong ? '#b56a00' : '#f59e0b';
}

function connectionShadeColor(shade: CanvasConnectionShade, focused: boolean, fill: boolean): string {
  if (shade.semanticStatus) {
    return connectionSemanticColor(shade.semanticStatus, focused, fill);
  }

  if (!focused) {
    return fill
      ? 'hsl(215 16% 45%)'
      : 'hsl(215 18% 56%)';
  }

  const harmony = connectionSourceHarmony(shade.sourceIndex);
  const variant = connectionMonochromeVariant(shade.variantIndex, shade.variantTotal);
  const baseLightness = fill
    ? 34
    : 47;
  const saturation = clampNumber(harmony.saturation + variant.saturationOffset + 7, 44, 78);
  const lightness = clampNumber(
    baseLightness + variant.lightnessOffset,
    fill ? 26 : 34,
    fill ? 48 : 58,
  );
  return `hsl(${harmony.hue.toFixed(1)} ${saturation}% ${lightness.toFixed(1)}%)`;
}

function connectionSemanticColor(
  status: CanvasConnectionSemanticStatus,
  focused: boolean,
  fill: boolean,
): string {
  if (status === 'completed') {
    return fill
      ? (focused ? '#027a48' : '#039855')
      : (focused ? '#039855' : '#12b76a');
  }

  if (status === 'failed') {
    return fill
      ? (focused ? '#b42318' : '#d92d20')
      : (focused ? '#d92d20' : '#f04438');
  }

  return fill
    ? (focused ? '#475467' : '#64748b')
    : (focused ? '#64748b' : '#94a3b8');
}

function connectionSourceHarmony(index: number): { hue: number; saturation: number } {
  const baseHue = 214;
  const pairOffsets = [
    0,
    -34,
    34,
    -58,
    58,
    -82,
    82,
    -112,
    112,
  ];
  const pairIndex = Math.floor(index / 2);
  const pairOffset = pairOffsets[pairIndex % pairOffsets.length]!;
  const wheelCycle = Math.floor(pairIndex / pairOffsets.length);
  const cycleShift = wheelCycle * 17;
  const hue = index % 2 === 0
    ? normalizeHue(baseHue + pairOffset + cycleShift)
    : normalizeHue(baseHue + 180 - pairOffset - cycleShift);

  return {
    hue,
    saturation: index % 2 === 0 ? 82 : 76,
  };
}

function connectionMonochromeVariant(index: number, total: number): { saturationOffset: number; lightnessOffset: number } {
  if (total <= 1) {
    return {
      saturationOffset: 0,
      lightnessOffset: 0,
    };
  }

  const normalized = index / Math.max(total - 1, 1);
  const spread = total <= 3 ? 18 : 26;
  const lightnessOffset = (normalized - 0.5) * spread;
  const saturationOffset = (0.5 - normalized) * 14;

  return {
    saturationOffset,
    lightnessOffset,
  };
}

function normalizeHue(hue: number): number {
  return ((hue % 360) + 360) % 360;
}

function clampNumber(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
