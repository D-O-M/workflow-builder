import { NodeModel, Point, Side } from './node-model';
import { CanvasWrapper } from './canvas-wrapper';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';

export interface ConnectionPropertyMappingModel {
  sourceProperty: string;
  targetProperty: string;
}

export type ConnectionBehaviorKind = 'plain' | 'mapped' | 'fanOut' | 'mappedFanOut';

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

const BEHAVIOR_DASH_PATTERNS: Record<ConnectionBehaviorKind, number[]> = {
  plain: [],
  mapped: [16, 8],
  fanOut: [3, 8],
  mappedFanOut: [16, 6, 3, 6],
};

export function describeConnectionBehavior(
  connection: ConnectionModel,
  allConnections: readonly ConnectionModel[],
): ConnectionBehaviorSummary {
  const siblingCount = allConnections.filter((candidate) =>
    candidate.from.id === connection.from.id &&
    candidate.anchor === connection.anchor,
  ).length;
  const isFanOut = siblingCount > 1;

  const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
  const targetProperties = Object.keys(connection.to.inputProperties)
    .sort((left, right) => left.localeCompare(right));
  const directTargetProperties = targetProperties.filter((targetProperty) =>
    Object.prototype.hasOwnProperty.call(sourceProperties, targetProperty),
  );
  const mappedPropertyCount = connection.propertyMappings.length;
  const directPropertyCount = connection.propertyMappings
    .filter((mapping) => mapping.sourceProperty === mapping.targetProperty)
    .length;
  const remappedPropertyCount = mappedPropertyCount - directPropertyCount;
  const explicitMappingsByTarget = new Map(connection.propertyMappings.map((mapping) =>
    [mapping.targetProperty, mapping.sourceProperty] as const,
  ));
  const isPlainFullTransfer =
    mappedPropertyCount === directTargetProperties.length &&
    directTargetProperties.every((targetProperty) => explicitMappingsByTarget.get(targetProperty) === targetProperty) &&
    connection.propertyMappings.every((mapping) => mapping.sourceProperty === mapping.targetProperty);
  const isMappedOrPartial = !isPlainFullTransfer;
  const kind: ConnectionBehaviorKind = isMappedOrPartial
    ? (isFanOut ? 'mappedFanOut' : 'mapped')
    : (isFanOut ? 'fanOut' : 'plain');
  const unmappedTargetCount = Math.max(targetProperties.length - mappedPropertyCount, 0);
  const summaryLabel = buildBehaviorSummaryLabel(kind, siblingCount, mappedPropertyCount, unmappedTargetCount);

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

export function buildConnectionMappingSelections(connection: ConnectionModel): Record<string, string> {
  const sourceProperties = connection.from.outputPropertiesForAnchor(connection.anchor);
  const explicitMappings = new Map(connection.propertyMappings.map((mapping) =>
    [mapping.targetProperty, mapping.sourceProperty] as const,
  ));
  const selections: Record<string, string> = {};

  for (const targetProperty of Object.keys(connection.to.inputProperties).sort((left, right) => left.localeCompare(right))) {
    selections[targetProperty] = explicitMappings.get(targetProperty)
      ?? (Object.prototype.hasOwnProperty.call(sourceProperties, targetProperty) ? targetProperty : '');
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
    .sort((left, right) => left.targetProperty.localeCompare(right.targetProperty));
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

  segments(options: PathfinderOptions, obstacles: NodeModel[] = []): { x1: number; y1: number; x2: number; y2: number }[] {
    const start = this.from.anchorPositions()[this.anchor];
    let end: Point;
    switch (this.side) {
      case 'left':
        end = { x: this.to.x, y: this.to.y + this.pos };
        break;
      case 'right':
        end = { x: this.to.x + this.to.width, y: this.to.y + this.pos };
        break;
      case 'top':
        end = { x: this.to.x + this.pos, y: this.to.y };
        break;
      default:
        end = { x: this.to.x + this.pos, y: this.to.y + this.to.height };
    }

    const arrowSize = 8;
    const tipX = Math.round(end.x / options.gridSize) * options.gridSize;
    const tipY = Math.round(end.y / options.gridSize) * options.gridSize;
    const baseX = tipX - (this.side === 'right' ? arrowSize : this.side === 'left' ? -arrowSize : 0);
    const baseY = tipY - (this.side === 'bottom' ? arrowSize : this.side === 'top' ? -arrowSize : 0);

    return orthogonalSegments(
      start.x,
      start.y,
      baseX,
      baseY,
      options,
      obstacles,
      this.side,
      this.to,
      this.from.anchorSide(this.anchor),
      this.from,
    );
  }

  badgePosition(options: PathfinderOptions, obstacles: NodeModel[] = []): Point | null {
    const segments = this.segments(options, obstacles);
    if (segments.length === 0) {
      return null;
    }

    const totalLength = segments.reduce((length, segment) =>
      length + Math.hypot(segment.x2 - segment.x1, segment.y2 - segment.y1), 0);
    if (totalLength === 0) {
      const lastSegment = segments[segments.length - 1];
      return { x: lastSegment.x2, y: lastSegment.y2 };
    }

    let remainingLength = totalLength / 2;
    for (const segment of segments) {
      const segmentLength = Math.hypot(segment.x2 - segment.x1, segment.y2 - segment.y1);
      if (segmentLength === 0) {
        continue;
      }

      if (remainingLength <= segmentLength) {
        const ratio = remainingLength / segmentLength;
        return {
          x: segment.x1 + (segment.x2 - segment.x1) * ratio,
          y: segment.y1 + (segment.y2 - segment.y1) * ratio,
        };
      }

      remainingLength -= segmentLength;
    }

    const lastSegment = segments[segments.length - 1];
    return { x: lastSegment.x2, y: lastSegment.y2 };
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
    const behavior = options?.behavior;

    wrapper.ctx.save();
    wrapper.ctx.strokeStyle = selected ? '#2563eb' : theme.connectionSoft;
    wrapper.ctx.fillStyle = selected ? '#2563eb' : theme.connection;
    wrapper.ctx.lineWidth = selected ? 4 : 2.5;
    wrapper.ctx.lineCap = 'round';
    wrapper.ctx.lineJoin = 'round';

    if (options?.showBehaviorCue && behavior) {
      wrapper.ctx.setLineDash(BEHAVIOR_DASH_PATTERNS[behavior.kind]);
    }

    const start = this.from.anchorPositions()[this.anchor];
    let end: Point;
    switch (this.side) {
      case 'left':
        end = { x: this.to.x, y: this.to.y + this.pos };
        break;
      case 'right':
        end = { x: this.to.x + this.to.width, y: this.to.y + this.pos };
        break;
      case 'top':
        end = { x: this.to.x + this.pos, y: this.to.y };
        break;
      default:
        end = { x: this.to.x + this.pos, y: this.to.y + this.to.height };
    }

    const tipX = wrapper.snap(end.x);
    const tipY = wrapper.snap(end.y);
    const baseX =
      tipX - (this.side === 'right' ? wrapper.arrowSize : this.side === 'left' ? -wrapper.arrowSize : 0);
    const baseY =
      tipY - (this.side === 'bottom' ? wrapper.arrowSize : this.side === 'top' ? -wrapper.arrowSize : 0);

    wrapper.drawOrthogonalLine(
      start.x,
      start.y,
      baseX,
      baseY,
      obstacles,
      this.side,
      this.to,
      this.from.anchorSide(this.anchor),
      this.from,
    );

    wrapper.ctx.setLineDash([]);
    wrapper.drawArrowHead(tipX, tipY, this.side);

    if (options?.showBehaviorCue && behavior?.showBadge) {
      const badge = this.badgePosition(wrapper.pathfinderOptions(), obstacles);
      if (badge) {
        wrapper.drawConnectionBadge(badge.x, badge.y, behavior.kind, selected);
      }
    }

    wrapper.ctx.restore();
  }

  hitTest(
    x: number,
    y: number,
    tolerance: number,
    options: PathfinderOptions,
    obstacles: NodeModel[] = [],
  ): boolean {
    return this.segments(options, obstacles).some((segment) =>
      distanceToSegment(x, y, segment.x1, segment.y1, segment.x2, segment.y2) <= tolerance,
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
