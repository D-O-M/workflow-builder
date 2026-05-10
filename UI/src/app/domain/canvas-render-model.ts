import {
  ConnectionBehaviorSummary,
  ConnectionModel,
  ConnectionPropertyMappingModel,
  resolveEffectivePropertyMappings,
} from './connection-model';
import {
  NodeAnchor,
  NodeChannelSectionLayout,
  NodeLayout,
  NodeModel,
  NodePortDefinition,
  NodeRenderStatus,
  NodeVisualKind,
  Point,
  Rect,
  Side,
  TaskPropertyType,
} from './node-model';
import {
  OrthogonalRouteResult,
  PathfinderOptions,
  RouteProblemSegment,
  RouteStatus,
  Segment,
  orthogonalRoute,
  subtractRouteProblemSegments,
} from './pathfinder';
import { ITaskPropertyDefinition } from '../interfaces/ITaskDefinition';

export type CanvasDetailTier = 'full' | 'simplified';
export type CanvasConnectionPresentation = 'normal' | 'ambient';
export type CanvasValidationSeverity = 'error' | 'warning';
type CanvasConnectionSemanticStatus = 'inactive' | 'completed' | 'failed';

export interface CanvasPortConnectivityIndex {
  readonly summaryPortKeys: ReadonlySet<string>;
  readonly propertyPortKeys: ReadonlySet<string>;
}

export interface CanvasBounds {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

export interface CanvasRenderSnapshot {
  readonly detailTier: CanvasDetailTier;
  readonly nodes: RenderedNode[];
  readonly connections: RenderedConnection[];
  readonly routeValidationErrors: readonly CanvasRouteValidationError[];
  readonly nodeById: ReadonlyMap<number, RenderedNode>;
  readonly connectionById: ReadonlyMap<string, RenderedConnection>;
  readonly portConnectivity: CanvasPortConnectivityIndex;
  getConnectedNodeIds(nodeId: number): ReadonlySet<number>;
  getConnectedNodes(nodeId: number): RenderedNode[];
  queryNodesAtPoint(x: number, y: number, exceptIds?: ReadonlySet<number>): RenderedNode[];
  queryConnectionsAtPoint(x: number, y: number): RenderedConnection[];
}

export interface CanvasRouteValidationError {
  readonly connectionId: string;
  readonly routeIndex: number;
  readonly message: string;
}

export const CONNECTION_STROKE_WIDTH = 3.2;
export const LANE_CENTER_SPACING = 7.2;

const CONNECTION_FOCUSED_STROKE_WIDTH = 8.2;
const CONNECTION_HALO_WIDTH = 18;

export interface CanvasConnectionLane {
  readonly offset: number;
  readonly index: number;
  readonly groupSize: number;
}

export interface CanvasConnectionShade {
  readonly index: number;
  readonly total: number;
  readonly sourceIndex: number;
  readonly sourceTotal: number;
  readonly variantIndex: number;
  readonly variantTotal: number;
  readonly semanticStatus?: CanvasConnectionSemanticStatus;
}

export class RenderedNode {
  readonly id: number;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly layout: NodeLayout;
  readonly inputChannelName: string | null;
  readonly outputChannelNames: readonly string[];
  readonly hasUpgradeWarning: boolean;
  readonly renderStatus: NodeRenderStatus | null;
  readonly failureMessage: string | null;
  readonly inputProperties: Readonly<Record<string, TaskPropertyType>>;
  readonly outputChannelProperties: Readonly<Record<string, Readonly<Record<string, TaskPropertyType>>>>;
  readonly designTimeInputOverrides: Readonly<Record<string, unknown>>;
  readonly inputPropertyDefinitions: readonly ITaskPropertyDefinition[];
  readonly inputChannelDisplayName: string | null;
  readonly outputChannelDisplayNames: readonly string[];
  readonly outputChannelPropertyDefinitions: Readonly<Record<string, readonly ITaskPropertyDefinition[]>>;
  readonly resolvedVersion: string | null;
  readonly name: string;
  readonly taskDefinitionId: number;
  readonly allowedMajor: number;
  readonly allowedMinor: number;
  readonly resolvedVersionId: number;
  private readonly visiblePortsList: readonly NodePortDefinition[];
  private readonly portByKey: ReadonlyMap<string, NodePortDefinition>;
  private readonly anchorDefinitionsList: readonly NodeAnchor[];
  private readonly interactionBoundsValue: CanvasBounds;
  readonly anySectionExpanded: boolean;
  readonly allSectionsExpanded: boolean;

  constructor(
    public readonly base: NodeModel,
    public readonly expandedSectionKeys: ReadonlySet<string>,
  ) {
    this.id = base.id;
    this.x = base.x;
    this.y = base.y;
    this.width = base.width;
    this.layout = base.layoutForExpandedSections(expandedSectionKeys);
    this.height = this.layout.totalHeight;
    this.inputChannelName = base.inputChannelName;
    this.outputChannelNames = base.outputChannelNames;
    this.hasUpgradeWarning = base.hasUpgradeWarning;
    this.renderStatus = base.renderStatus;
    this.failureMessage = base.failureMessage;
    this.inputProperties = base.inputProperties;
    this.outputChannelProperties = base.outputChannelProperties;
    this.designTimeInputOverrides = base.designTimeInputOverrides;
    this.inputPropertyDefinitions = base.inputPropertyDefinitions;
    this.inputChannelDisplayName = base.inputChannelDisplayName;
    this.outputChannelDisplayNames = base.outputChannelDisplayNames;
    this.outputChannelPropertyDefinitions = base.outputChannelPropertyDefinitions;
    this.resolvedVersion = base.resolvedVersion;
    this.name = base.name;
    this.taskDefinitionId = base.taskDefinitionId;
    this.allowedMajor = base.allowedMajor;
    this.allowedMinor = base.allowedMinor;
    this.resolvedVersionId = base.resolvedVersionId;
    this.visiblePortsList = [...this.layout.summaryPorts, ...this.layout.propertyPorts];
    this.portByKey = new Map(this.visiblePortsList.map((port) => [port.key, port] as const));
    this.anchorDefinitionsList = this.buildAnchorDefinitions();
    this.interactionBoundsValue = this.computeInteractionBounds();
    const sectionCount = (this.layout.inputSection ? 1 : 0) + this.layout.outputSections.length;
    this.anySectionExpanded = this.sections().some((section) => section.expanded);
    this.allSectionsExpanded = sectionCount > 0 && this.sections().every((section) => section.expanded);
  }

  get expanded(): boolean {
    return this.anySectionExpanded;
  }

  get visualKind(): NodeVisualKind {
    return this.base.visualKind;
  }

  get isCollectionIteratorOperator(): boolean {
    return this.base.isCollectionIteratorOperator;
  }

  get isDataBridge(): boolean {
    return this.base.isDataBridge;
  }

  contains(x: number, y: number): boolean {
    return x >= this.x && x <= this.x + this.width && y >= this.y && y <= this.y + this.height;
  }

  containsBuffered(x: number, y: number, buffer: number): boolean {
    return (
      x >= this.x - buffer &&
      x <= this.x + this.width + buffer &&
      y >= this.y - buffer &&
      y <= this.y + this.height + buffer
    );
  }

  containsInteractionPoint(x: number, y: number): boolean {
    return this.contains(x, y) || this.portHit(x, y, 0) !== null;
  }

  interactionBounds(): CanvasBounds {
    return this.interactionBoundsValue;
  }

  isFullyWithin(left: number, top: number, right: number, bottom: number): boolean {
    return (
      this.x >= left &&
      this.y >= top &&
      this.x + this.width <= right &&
      this.y + this.height <= bottom
    );
  }

  toggleRect(): { x: number; y: number; width: number; height: number } {
    return this.layout.toggleRect;
  }

  settingsRect(): { x: number; y: number; width: number; height: number } {
    return this.layout.toggleRect;
  }

  visiblePorts(): readonly NodePortDefinition[] {
    return this.visiblePortsList;
  }

  portDefinition(portKey: string): NodePortDefinition | null {
    return this.portByKey.get(portKey) ?? null;
  }

  portHit(x: number, y: number, extraHitRadius = 5): NodePortDefinition | null {
    for (let index = this.visiblePortsList.length - 1; index >= 0; index -= 1) {
      const port = this.visiblePortsList[index]!;
      if (pointInsideExpandedRect(x, y, port.hitBounds, extraHitRadius)) {
        return port;
      }

      if (Math.hypot(x - port.x, y - port.y) <= port.radius + extraHitRadius) {
        return port;
      }
    }

    return null;
  }

  sectionToggleHit(x: number, y: number): string | null {
    if (this.isCollectionIteratorOperator) {
      return null;
    }

    const section = this.sections().find((candidate) =>
      x >= candidate.toggleRect.x &&
      x <= candidate.toggleRect.x + candidate.toggleRect.width &&
      y >= candidate.toggleRect.y &&
      y <= candidate.toggleRect.y + candidate.toggleRect.height,
    );
    return section?.key ?? null;
  }

  sectionHeaderHit(x: number, y: number): string | null {
    if (this.isCollectionIteratorOperator) {
      return null;
    }

    const section = this.sections().find((candidate) =>
      x >= this.x + 14 &&
      x <= this.x + this.width - 14 &&
      y >= candidate.headerTop &&
      y <= candidate.headerTop + candidate.headerHeight,
    );
    return section?.key ?? null;
  }

  allSectionKeys(): string[] {
    if (this.isCollectionIteratorOperator) {
      return [];
    }

    return this.sections().map((section) => section.key);
  }

  sectionForPort(port: NodePortDefinition): NodeChannelSectionLayout | null {
    return this.sections().find((section) => section.key === `${port.side}:${port.channelName}`) ?? null;
  }

  inputPropertyPort(propertyName: string): NodePortDefinition | null {
    return this.layout.inputSection?.propertyRows.find((row) => row.name === propertyName)?.port ?? null;
  }

  outputPropertyPort(anchorIndex: number, propertyName: string): NodePortDefinition | null {
    const section = this.outputSectionForAnchor(anchorIndex);
    return section?.propertyRows.find((row) => row.name === propertyName)?.port ?? null;
  }

  outputSummaryPort(anchorIndex: number): NodePortDefinition | null {
    return this.outputSectionForAnchor(anchorIndex)?.summaryPort ?? null;
  }

  inputSummaryPort(): NodePortDefinition | null {
    return this.layout.inputSection?.summaryPort ?? null;
  }

  inputAnchorIndex(): number | null {
    return this.anchorDefinitionsList.find((anchor) => anchor.kind === 'input')?.index ?? null;
  }

  outputAnchors(): readonly NodeAnchor[] {
    return this.anchorDefinitionsList.filter((anchor) => anchor.kind === 'output');
  }

  anchorDefinitions(): readonly NodeAnchor[] {
    return this.anchorDefinitionsList;
  }

  anchorDefinition(idx: number): NodeAnchor | null {
    return this.anchorDefinitionsList.find((anchor) => anchor.index === idx) ?? null;
  }

  anchorSide(idx: number): Side {
    return this.anchorDefinition(idx)?.side ?? 'right';
  }

  anchorRadius(idx: number): number {
    return this.anchorDefinition(idx)?.kind === 'input' ? 8 : 7;
  }

  outputChannelNameForAnchor(idx: number): string | null {
    const outputIndex = idx - (this.inputChannelName ? 1 : 0);
    return outputIndex >= 0 ? this.outputChannelNames[outputIndex] ?? null : null;
  }

  outputPropertiesForAnchor(idx: number): Record<string, TaskPropertyType> {
    const channelName = this.outputChannelNameForAnchor(idx);
    return channelName ? this.base.outputPropertiesForAnchor(idx) : {};
  }

  outputPropertyDefinitionsForAnchor(idx: number): ITaskPropertyDefinition[] {
    return this.base.outputPropertyDefinitionsForAnchor(idx);
  }

  inputAnchorSnap(): { x: number; y: number; side: Side; pos: number } | null {
    const anchor = this.inputSummaryPort();
    if (!anchor) {
      return null;
    }

    return {
      x: anchor.x,
      y: anchor.y,
      side: 'left',
      pos: anchor.y - this.y,
    };
  }

  sideFromPoint(x: number, y: number): Side {
    const cx = this.x + this.width / 2;
    const cy = this.y + this.height / 2;
    const dx = x - cx;
    const dy = y - cy;
    if (Math.abs(dx) * this.height > Math.abs(dy) * this.width) {
      return dx < 0 ? 'left' : 'right';
    }

    return dy < 0 ? 'top' : 'bottom';
  }

  snapPointFixedSide(x: number, y: number, side: Side): { x: number; y: number; side: Side; pos: number } {
    switch (side) {
      case 'left': {
        const yy = NodeModel.clamp(y, this.y, this.y + this.height);
        return { x: this.x, y: yy, side: 'left', pos: yy - this.y };
      }
      case 'right': {
        const yy = NodeModel.clamp(y, this.y, this.y + this.height);
        return { x: this.x + this.width, y: yy, side: 'right', pos: yy - this.y };
      }
      case 'top': {
        const xx = NodeModel.clamp(x, this.x, this.x + this.width);
        return { x: xx, y: this.y, side: 'top', pos: xx - this.x };
      }
      default: {
        const xx = NodeModel.clamp(x, this.x, this.x + this.width);
        return { x: xx, y: this.y + this.height, side: 'bottom', pos: xx - this.x };
      }
    }
  }

  get designTimeOverrideCount(): number {
    return this.base.designTimeOverrideCount;
  }

  get hasDesignTimeOverrides(): boolean {
    return this.base.hasDesignTimeOverrides;
  }

  get inputChannelLabel(): string | null {
    return this.base.inputChannelLabel;
  }

  designTimeOverrideKeys(): string[] {
    return this.base.designTimeOverrideKeys();
  }

  private outputSectionForAnchor(idx: number): NodeChannelSectionLayout | null {
    const outputIndex = idx - (this.inputChannelName ? 1 : 0);
    return outputIndex >= 0 ? this.layout.outputSections[outputIndex] ?? null : null;
  }

  private sections(): NodeChannelSectionLayout[] {
    return [
      ...(this.layout.inputSection ? [this.layout.inputSection] : []),
      ...this.layout.outputSections,
    ];
  }

  private buildAnchorDefinitions(): NodeAnchor[] {
    const anchors: NodeAnchor[] = [];
    let index = 0;

    if (this.layout.inputSection) {
      anchors.push({
        index,
        kind: 'input',
        label: this.layout.inputSection.label,
        side: 'left',
        x: this.layout.inputSection.summaryPort.x,
        y: this.layout.inputSection.summaryPort.y,
      });
      index += 1;
    }

    for (const section of this.layout.outputSections) {
      anchors.push({
        index,
        kind: 'output',
        label: section.label,
        side: 'right',
        x: section.summaryPort.x,
        y: section.summaryPort.y,
      });
      index += 1;
    }

    return anchors;
  }

  private computeInteractionBounds(): CanvasBounds {
    let bounds: CanvasBounds = {
      left: this.x,
      top: this.y,
      right: this.x + this.width,
      bottom: this.y + this.height,
    };

    for (const port of this.visiblePortsList) {
      bounds = unionBounds(bounds, portBounds(port));
    }

    return bounds;
  }
}

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
  ) {
    this.id = base.id;
    this.anchor = base.anchor;
    this.side = base.side;
    this.pos = base.pos;
    this.propertyMappings = base.propertyMappings;
    this.behavior = behavior;
    this.showBehaviorCue = showBehaviorCue && detailTier === 'full';
    this.effectiveMappings = resolveEffectivePropertyMappings(base);
    this.routes = buildConnectionVisualRoutes(
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
        ? [{ connectionId: this.id, routeIndex, message: route.validationMessage }]
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
    const selected = options?.selected ?? false;
    const hovered = options?.hovered ?? false;
    const dimmed = options?.dimmed ?? false;
    const presentation = options?.presentation ?? 'normal';
    const hoveredRouteIndex = options?.hoveredRouteIndex ?? null;
    const validationSeverity = options?.validationSeverity ?? null;
    const hasRouteFocus = hovered && hoveredRouteIndex !== null;
    const ambient = presentation === 'ambient';
    const validationFocused = validationSeverity !== null;
    const focused = ambient
      ? validationFocused
      : selected || hovered || validationFocused;

    wrapper.ctx.save();
    const hasProblemRoutes = !ambient && this.routes.some((route) => route.status === 'obstacleViolation');
    const problemStroke = '#d92d20';
    const problemSoft = 'rgba(217, 45, 32, 0.72)';
    const ambientStroke = 'rgba(100, 116, 139, 0.85)';
    const validationStroke = validationSeverity ? validationConnectionColor(validationSeverity, true) : null;
    const validationSoft = validationSeverity ? validationConnectionColor(validationSeverity, false) : null;

    wrapper.ctx.strokeStyle = hasProblemRoutes
      ? problemSoft
      : validationSoft ?? (ambient ? ambientStroke : connectionShadeStroke(this.routes[0]?.shade ?? DEFAULT_CONNECTION_SHADE, focused));
    wrapper.ctx.fillStyle = hasProblemRoutes
      ? problemStroke
      : validationStroke ?? (ambient ? ambientStroke : connectionShadeFill(this.routes[0]?.shade ?? DEFAULT_CONNECTION_SHADE, focused));
    wrapper.ctx.lineCap = ambient ? 'round' : 'butt';
    wrapper.ctx.lineJoin = 'round';

    if (focused && !ambient) {
      wrapper.ctx.save();
      wrapper.ctx.globalAlpha = hasProblemRoutes ? 0.13 : 0.12;
      const haloWidth = CONNECTION_HALO_WIDTH;
      wrapper.ctx.lineWidth = haloWidth;
      wrapper.ctx.setLineDash([]);
      for (const [routeIndex, route] of this.routes.entries()) {
        if (hasRouteFocus && routeIndex !== hoveredRouteIndex) {
          continue;
        }

        wrapper.ctx.strokeStyle = route.status === 'obstacleViolation'
          ? problemStroke
          : validationStroke
            ? validationStroke
            : connectionShadeStroke(route.shade, true);
        wrapper.ctx.fillStyle = wrapper.ctx.strokeStyle;
        strokeContinuousRoute(wrapper.ctx, route.visualSegments);
        fillRouteEndpointCaps(wrapper.ctx, route, haloWidth);
      }
      wrapper.ctx.restore();
    }

    for (const [routeIndex, route] of this.routes.entries()) {
      if (route.visualSegments.length === 0) {
        continue;
      }

      const routeFocused = focused && (!hasRouteFocus || routeIndex === hoveredRouteIndex);
      const routeDimmed = dimmed || (hasRouteFocus && routeIndex !== hoveredRouteIndex);
      const lineWidth = ambient
        ? validationFocused ? 1.8 : 1.4
        : routeFocused ? CONNECTION_FOCUSED_STROKE_WIDTH : CONNECTION_STROKE_WIDTH;
      const routeStroke = route.status === 'obstacleViolation'
        ? (ambient ? ambientStroke : problemSoft)
        : validationSoft ?? (ambient ? ambientStroke : connectionShadeStroke(route.shade, routeFocused));

      wrapper.ctx.save();
      wrapper.ctx.lineWidth = lineWidth;
      wrapper.ctx.globalAlpha = ambient
        ? validationFocused ? 0.72 : routeDimmed ? 0.1 : 0.18
        : routeDimmed
          ? 0.13
          : routeFocused
            ? 1
            : route.status === 'obstacleViolation'
              ? 0.9
              : 0.42;
      wrapper.ctx.strokeStyle = routeStroke;
      wrapper.ctx.fillStyle = routeStroke;

      if (!ambient && route.visualProblemSegments.length > 0) {
        strokeSegments(wrapper.ctx, subtractRouteProblemSegments(route.visualSegments, route.visualProblemSegments));
        wrapper.ctx.save();
        wrapper.ctx.strokeStyle = problemStroke;
        wrapper.ctx.lineWidth = routeFocused ? 10 : 6.8;
        wrapper.ctx.setLineDash([8, 6]);
        strokeSegments(wrapper.ctx, route.visualProblemSegments);
        wrapper.ctx.restore();
      } else {
        strokeContinuousRoute(wrapper.ctx, route.visualSegments);
      }

      if (!ambient) {
        fillRouteEndpointCaps(wrapper.ctx, route, lineWidth);
      }
      wrapper.ctx.restore();
    }

    wrapper.ctx.restore();
  }

  hitTest(x: number, y: number, tolerance: number): boolean {
    return this.distanceToVisualRoute(x, y, tolerance) <= tolerance;
  }

  distanceToVisualRoute(x: number, y: number, boundsPadding = 0): number {
    return this.nearestVisualRoute(x, y, boundsPadding)?.distance ?? Number.POSITIVE_INFINITY;
  }

  nearestVisualRoute(x: number, y: number, boundsPadding = 0): { routeIndex: number; distance: number } | null {
    if (this.routeBounds && !boundsContainPoint(this.routeBounds, x, y, boundsPadding)) {
      return null;
    }

    let nearestDistance = Number.POSITIVE_INFINITY;
    let nearestRouteIndex = -1;
    for (const [routeIndex, route] of this.routes.entries()) {
      for (const segment of route.visualSegments) {
        const distance = distanceToSegment(x, y, segment.x1, segment.y1, segment.x2, segment.y2);
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
    return !!this.badgePosition && Math.hypot(x - this.badgePosition.x, y - this.badgePosition.y) <= tolerance;
  }
}

export interface RenderedConnectionRoute {
  readonly segments: readonly Segment[];
  readonly visualSegments: readonly Segment[];
  readonly status: RouteStatus;
  readonly problemSegments: readonly RouteProblemSegment[];
  readonly visualProblemSegments: readonly RouteProblemSegment[];
  readonly validationMessage?: string;
  readonly tip: Point;
  readonly startSide: Side;
  readonly side: Side;
  readonly bounds: CanvasBounds | null;
  readonly canonicalBounds: CanvasBounds | null;
  readonly lane: CanvasConnectionLane;
  readonly shade: CanvasConnectionShade;
  readonly startPortKey: string;
  readonly endPortKey: string;
  readonly sourceProperty: string | null;
  readonly targetProperty: string | null;
}

export function buildCanvasRenderSnapshot(
  nodes: readonly NodeModel[],
  connections: readonly ConnectionModel[],
  expandedNodeSections: ReadonlyMap<number, ReadonlySet<string>>,
  detailTier: CanvasDetailTier,
  pathfinderOptions: PathfinderOptions,
  readOnly: boolean,
): CanvasRenderSnapshot {
  const renderedNodes = nodes.map((node) => new RenderedNode(node, expandedNodeSections.get(node.id) ?? EMPTY_SECTION_KEY_SET));
  const nodeById = new Map(renderedNodes.map((node) => [node.id, node] as const));
  const summaryPortKeys = new Set<string>();
  const propertyPortKeys = new Set<string>();
  const connectedNodeIdsByNodeId = new Map<number, Set<number>>();
  const renderedConnections: RenderedConnection[] = [];
  const routeValidationErrors: CanvasRouteValidationError[] = [];
  const laneByConnectionId = buildConnectionLaneAssignments(connections);
  const shadeByConnectionId = buildConnectionShadeAssignments(connections, nodeById, detailTier);

  for (const connection of connections) {
    const from = nodeById.get(connection.from.id);
    const to = nodeById.get(connection.to.id);
    if (!from || !to) {
      continue;
    }

    const obstacles = renderedNodes.filter((node) => node.id !== from.id && node.id !== to.id);
    const renderedConnection = new RenderedConnection(
      connection,
      from,
      to,
      detailTier,
      pathfinderOptions,
      obstacles,
      null,
      false,
      laneByConnectionId.get(connection.id) ?? DEFAULT_CONNECTION_LANE,
      connectionShadeForMode(
        shadeByConnectionId.get(connection.id) ?? DEFAULT_CONNECTION_SHADE,
        from.renderStatus,
        readOnly,
      ),
    );
    renderedConnections.push(renderedConnection);
    routeValidationErrors.push(...renderedConnection.routeValidationErrors);

    const sourceSummaryPort = from.outputSummaryPort(connection.anchor);
    if (sourceSummaryPort) {
      summaryPortKeys.add(portScopedKey(from, sourceSummaryPort));
    }

    const targetSummaryPort = to.inputSummaryPort();
    if (targetSummaryPort) {
      summaryPortKeys.add(portScopedKey(to, targetSummaryPort));
    }

    for (const mapping of renderedConnection.effectiveMappings) {
      const sourcePropertyPort = from.outputPropertyPort(connection.anchor, mapping.sourceProperty);
      if (sourcePropertyPort) {
        propertyPortKeys.add(portScopedKey(from, sourcePropertyPort));
      }

      const targetPropertyPort = to.inputPropertyPort(mapping.targetProperty);
      if (targetPropertyPort) {
        propertyPortKeys.add(portScopedKey(to, targetPropertyPort));
      }
    }

    addConnectedNodeId(connectedNodeIdsByNodeId, from.id, to.id);
    addConnectedNodeId(connectedNodeIdsByNodeId, to.id, from.id);
  }

  const nodeIndex = new SpatialIndex<RenderedNode>(360);
  for (const node of renderedNodes) {
    nodeIndex.insert(node.interactionBounds(), node);
  }

  const connectionIndex = new SpatialIndex<RenderedConnection>(360);
  for (const connection of renderedConnections) {
    if (connection.routeBounds) {
      connectionIndex.insert(connection.routeBounds, connection);
    }
  }

  return {
    detailTier,
    nodes: renderedNodes,
    connections: renderedConnections,
    routeValidationErrors,
    nodeById,
    connectionById: new Map(renderedConnections.map((connection) => [connection.id, connection] as const)),
    portConnectivity: {
      summaryPortKeys,
      propertyPortKeys,
    },
    getConnectedNodeIds(nodeId: number): ReadonlySet<number> {
      return connectedNodeIdsByNodeId.get(nodeId) ?? EMPTY_NODE_ID_SET;
    },
    getConnectedNodes(nodeId: number): RenderedNode[] {
      const nodeIds = connectedNodeIdsByNodeId.get(nodeId);
      if (!nodeIds || nodeIds.size === 0) {
        return [];
      }

      return [...nodeIds]
        .map((connectedId) => nodeById.get(connectedId))
        .filter((node): node is RenderedNode => !!node);
    },
    queryNodesAtPoint(x: number, y: number, exceptIds: ReadonlySet<number> = EMPTY_NODE_ID_SET): RenderedNode[] {
      return nodeIndex.queryPoint(x, y).filter((node) => !exceptIds.has(node.id));
    },
    queryConnectionsAtPoint(x: number, y: number): RenderedConnection[] {
      return connectionIndex.queryPoint(x, y);
    },
  };
}

class SpatialIndex<T> {
  private readonly buckets = new Map<string, T[]>();

  constructor(private readonly bucketSize: number) {}

  insert(bounds: CanvasBounds, item: T): void {
    const [startX, endX] = this.bucketRange(bounds.left, bounds.right);
    const [startY, endY] = this.bucketRange(bounds.top, bounds.bottom);

    for (let bucketX = startX; bucketX <= endX; bucketX += 1) {
      for (let bucketY = startY; bucketY <= endY; bucketY += 1) {
        const key = `${bucketX}:${bucketY}`;
        const bucket = this.buckets.get(key);
        if (bucket) {
          bucket.push(item);
        } else {
          this.buckets.set(key, [item]);
        }
      }
    }
  }

  queryPoint(x: number, y: number): T[] {
    const bucket = this.buckets.get(`${Math.floor(x / this.bucketSize)}:${Math.floor(y / this.bucketSize)}`);
    if (!bucket || bucket.length === 0) {
      return [];
    }

    return [...new Set(bucket)];
  }

  private bucketRange(min: number, max: number): [number, number] {
    return [Math.floor(min / this.bucketSize), Math.floor(max / this.bucketSize)];
  }
}

function buildConnectionVisualRoutes(
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

function buildConnectionLaneAssignments(connections: readonly ConnectionModel[]): ReadonlyMap<string, CanvasConnectionLane> {
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

function buildConnectionShadeAssignments(
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

function offsetConnectionShade(shade: CanvasConnectionShade, routeIndex: number): CanvasConnectionShade {
  return {
    ...shade,
    index: shade.index + routeIndex,
    variantIndex: shade.variantIndex + routeIndex,
  };
}

function connectionShadeForMode(
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

function connectionShadeStroke(shade: CanvasConnectionShade, focused: boolean): string {
  return connectionShadeColor(shade, focused, false);
}

function connectionShadeFill(shade: CanvasConnectionShade, focused: boolean): string {
  return connectionShadeColor(shade, focused, true);
}

function validationConnectionColor(severity: CanvasValidationSeverity, strong: boolean): string {
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

function createLaneAdjustedSegments(
  segments: readonly Segment[],
  laneOffset: number,
): readonly Segment[] {
  if (segments.length < 3 || segments.length > 7 || laneOffset === 0) {
    return segments;
  }

  const first = segments[0]!;
  const last = segments[segments.length - 1]!;
  const middleSegments = segments.slice(1, -1);
  if (middleSegments.length === 0) {
    return segments;
  }

  const shiftedMiddlePoints = createOffsetCorridorPoints(middleSegments, laneOffset);
  if (shiftedMiddlePoints.length < 2) {
    return segments;
  }

  const adjusted: Segment[] = [{ ...first }];
  appendSegmentsBetweenPoints(adjusted, { x: first.x2, y: first.y2 }, shiftedMiddlePoints[0]!);
  for (let index = 0; index < shiftedMiddlePoints.length - 1; index += 1) {
    appendSegmentsBetweenPoints(adjusted, shiftedMiddlePoints[index]!, shiftedMiddlePoints[index + 1]!);
  }
  appendSegmentsBetweenPoints(adjusted, shiftedMiddlePoints[shiftedMiddlePoints.length - 1]!, { x: last.x1, y: last.y1 });
  adjusted.push({ ...last });

  return laneAdjustmentIsSafe(segments, adjusted) ? adjusted : segments;
}

function createLaneAdjustedProblemSegments(
  problemSegments: readonly RouteProblemSegment[],
  canonicalSegments: readonly Segment[],
  laneOffset: number,
): readonly RouteProblemSegment[] {
  if (problemSegments.length === 0 || laneOffset === 0) {
    return problemSegments;
  }

  const firstSegment = canonicalSegments[0] ?? null;
  const lastSegment = canonicalSegments[canonicalSegments.length - 1] ?? null;
  return problemSegments.map((segment) => {
    if ((firstSegment && isSameAxisSubSegment(segment, firstSegment)) ||
      (lastSegment && isSameAxisSubSegment(segment, lastSegment))) {
      return segment;
    }

    return {
      ...segment,
      ...offsetSegmentPerpendicular(segment, laneOffset),
    };
  });
}

function createOffsetCorridorPoints(segments: readonly Segment[], laneOffset: number): Point[] {
  const shiftedSegments = segments.map((segment) => offsetSegmentPerpendicular(segment, laneOffset));
  const points: Point[] = [{ x: shiftedSegments[0]!.x1, y: shiftedSegments[0]!.y1 }];

  for (let index = 0; index < shiftedSegments.length - 1; index += 1) {
    const current = shiftedSegments[index]!;
    const next = shiftedSegments[index + 1]!;
    const intersection = orthogonalLineIntersection(current, next);
    points.push(intersection ?? { x: current.x2, y: current.y2 });
  }

  const last = shiftedSegments[shiftedSegments.length - 1]!;
  points.push({ x: last.x2, y: last.y2 });
  return removeConsecutiveDuplicatePoints(points);
}

function orthogonalLineIntersection(first: Segment, second: Segment): Point | null {
  const firstHorizontal = first.y1 === first.y2;
  const secondHorizontal = second.y1 === second.y2;
  if (firstHorizontal === secondHorizontal) {
    return null;
  }

  return firstHorizontal
    ? { x: second.x1, y: first.y1 }
    : { x: first.x1, y: second.y1 };
}

function appendSegmentsBetweenPoints(segments: Segment[], start: Point, end: Point): void {
  if (start.x === end.x || start.y === end.y) {
    appendSegment(segments, { x1: start.x, y1: start.y, x2: end.x, y2: end.y });
    return;
  }

  const elbow = { x: end.x, y: start.y };
  appendSegment(segments, { x1: start.x, y1: start.y, x2: elbow.x, y2: elbow.y });
  appendSegment(segments, { x1: elbow.x, y1: elbow.y, x2: end.x, y2: end.y });
}

function laneAdjustmentIsSafe(canonicalSegments: readonly Segment[], visualSegments: readonly Segment[]): boolean {
  const canonicalFirst = canonicalSegments[0];
  const canonicalLast = canonicalSegments[canonicalSegments.length - 1];
  const visualFirst = visualSegments[0];
  const visualLast = visualSegments[visualSegments.length - 1];
  if (!canonicalFirst || !canonicalLast || !visualFirst || !visualLast) {
    return false;
  }

  if (!segmentsShareEndpoints(canonicalFirst, visualFirst) || !segmentsShareEndpoints(canonicalLast, visualLast)) {
    return false;
  }

  if (!visualSegments.every(isCardinalSegment)) {
    return false;
  }

  const points = routeSegmentsToPoints(visualSegments);
  if (hasImmediateBacktrack(points) || hasRepeatedPoint(points) || hasOppositeConsecutiveSegments(visualSegments)) {
    return false;
  }

  return routeTurnCount(visualSegments) <= routeTurnCount(canonicalSegments) + 4;
}

function segmentsShareEndpoints(left: Segment, right: Segment): boolean {
  return left.x1 === right.x1 && left.y1 === right.y1 && left.x2 === right.x2 && left.y2 === right.y2;
}

function isCardinalSegment(segment: Segment): boolean {
  return segment.x1 === segment.x2 || segment.y1 === segment.y2;
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

function hasImmediateBacktrack(points: readonly Point[]): boolean {
  for (let index = 2; index < points.length; index += 1) {
    if (points[index]!.x === points[index - 2]!.x && points[index]!.y === points[index - 2]!.y) {
      return true;
    }
  }

  return false;
}

function hasRepeatedPoint(points: readonly Point[]): boolean {
  const seen = new Set<string>();
  for (const point of points) {
    const key = `${point.x}:${point.y}`;
    if (seen.has(key)) {
      return true;
    }

    seen.add(key);
  }

  return false;
}

function hasOppositeConsecutiveSegments(segments: readonly Segment[]): boolean {
  for (let index = 1; index < segments.length; index += 1) {
    const previous = segments[index - 1]!;
    const current = segments[index]!;
    if (previous.y1 === previous.y2 && current.y1 === current.y2 && previous.y1 === current.y1) {
      const previousDirection = Math.sign(previous.x2 - previous.x1);
      const currentDirection = Math.sign(current.x2 - current.x1);
      if (previousDirection !== 0 && currentDirection !== 0 && previousDirection === -currentDirection) {
        return true;
      }
    }

    if (previous.x1 === previous.x2 && current.x1 === current.x2 && previous.x1 === current.x1) {
      const previousDirection = Math.sign(previous.y2 - previous.y1);
      const currentDirection = Math.sign(current.y2 - current.y1);
      if (previousDirection !== 0 && currentDirection !== 0 && previousDirection === -currentDirection) {
        return true;
      }
    }
  }

  return false;
}

function removeConsecutiveDuplicatePoints(points: readonly Point[]): Point[] {
  const result: Point[] = [];
  for (const point of points) {
    const previous = result[result.length - 1];
    if (previous && previous.x === point.x && previous.y === point.y) {
      continue;
    }

    result.push(point);
  }

  return result;
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

function appendSegment(segments: Segment[], segment: Segment): void {
  if (segment.x1 === segment.x2 && segment.y1 === segment.y2) {
    return;
  }

  const previous = segments[segments.length - 1];
  if (previous && previous.x2 === segment.x1 && previous.y2 === segment.y1) {
    if (previous.y1 === previous.y2 && segment.y1 === segment.y2) {
      previous.x2 = segment.x2;
      return;
    }

    if (previous.x1 === previous.x2 && segment.x1 === segment.x2) {
      previous.y2 = segment.y2;
      return;
    }
  }

  segments.push({ ...segment });
}

function offsetSegmentPerpendicular(segment: Segment, laneOffset: number): Segment {
  if (segment.y1 === segment.y2) {
    return {
      x1: segment.x1,
      y1: segment.y1 + laneOffset,
      x2: segment.x2,
      y2: segment.y2 + laneOffset,
    };
  }

  return {
    x1: segment.x1 + laneOffset,
    y1: segment.y1,
    x2: segment.x2 + laneOffset,
    y2: segment.y2,
  };
}

function isSameAxisSubSegment(candidate: Segment, container: Segment): boolean {
  if (candidate.y1 === candidate.y2 && container.y1 === container.y2 && candidate.y1 === container.y1) {
    return rangesOverlap(candidate.x1, candidate.x2, container.x1, container.x2);
  }

  if (candidate.x1 === candidate.x2 && container.x1 === container.x2 && candidate.x1 === container.x1) {
    return rangesOverlap(candidate.y1, candidate.y2, container.y1, container.y2);
  }

  return false;
}

function rangesOverlap(a1: number, a2: number, b1: number, b2: number): boolean {
  const aMin = Math.min(a1, a2);
  const aMax = Math.max(a1, a2);
  const bMin = Math.min(b1, b2);
  const bMax = Math.max(b1, b2);
  return aMin >= bMin && aMax <= bMax;
}

function portBounds(port: NodePortDefinition): CanvasBounds {
  if (port.hitBounds) {
    return rectToBounds(port.hitBounds);
  }

  return {
    left: port.x - port.radius,
    top: port.y - port.radius,
    right: port.x + port.radius,
    bottom: port.y + port.radius,
  };
}

function portScopedKey(node: RenderedNode, port: NodePortDefinition): string {
  return `${node.id}:${port.key}`;
}

function rectToBounds(rect: Rect): CanvasBounds {
  return {
    left: rect.x,
    top: rect.y,
    right: rect.x + rect.width,
    bottom: rect.y + rect.height,
  };
}

function unionBounds(left: CanvasBounds, right: CanvasBounds): CanvasBounds {
  return {
    left: Math.min(left.left, right.left),
    top: Math.min(left.top, right.top),
    right: Math.max(left.right, right.right),
    bottom: Math.max(left.bottom, right.bottom),
  };
}

function pointInsideExpandedRect(x: number, y: number, rect: Rect | undefined, padding: number): boolean {
  return !!rect &&
    x >= rect.x - padding &&
    x <= rect.x + rect.width + padding &&
    y >= rect.y - padding &&
    y <= rect.y + rect.height + padding;
}

function unionRouteBounds(routes: readonly RenderedConnectionRoute[]): CanvasBounds | null {
  let left = Number.POSITIVE_INFINITY;
  let top = Number.POSITIVE_INFINITY;
  let right = Number.NEGATIVE_INFINITY;
  let bottom = Number.NEGATIVE_INFINITY;

  for (const route of routes) {
    if (!route.bounds) {
      continue;
    }

    left = Math.min(left, route.bounds.left);
    top = Math.min(top, route.bounds.top);
    right = Math.max(right, route.bounds.right);
    bottom = Math.max(bottom, route.bounds.bottom);
  }

  return Number.isFinite(left) ? { left, top, right, bottom } : null;
}

function unionSegmentBounds(segments: readonly Segment[]): CanvasBounds | null {
  if (segments.length === 0) {
    return null;
  }

  let left = Number.POSITIVE_INFINITY;
  let top = Number.POSITIVE_INFINITY;
  let right = Number.NEGATIVE_INFINITY;
  let bottom = Number.NEGATIVE_INFINITY;

  for (const segment of segments) {
    left = Math.min(left, segment.x1, segment.x2);
    top = Math.min(top, segment.y1, segment.y2);
    right = Math.max(right, segment.x1, segment.x2);
    bottom = Math.max(bottom, segment.y1, segment.y2);
  }

  return { left, top, right, bottom };
}

function strokeContinuousRoute(ctx: CanvasRenderingContext2D, segments: readonly Segment[]): void {
  if (segments.length === 0) {
    return;
  }

  const points = routeSegmentsToPoints(segments);
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

    if (pointsAreCollinear(previous, current, next)) {
      ctx.lineTo(current.x, current.y);
      continue;
    }

    const radius = Math.min(
      cornerRadius,
      distanceBetweenPoints(previous, current) / 2,
      distanceBetweenPoints(current, next) / 2,
    );
    if (radius <= 0) {
      ctx.lineTo(current.x, current.y);
      continue;
    }

    const cornerStart = pointToward(current, previous, radius);
    const cornerEnd = pointToward(current, next, radius);
    ctx.lineTo(cornerStart.x, cornerStart.y);
    ctx.quadraticCurveTo(current.x, current.y, cornerEnd.x, cornerEnd.y);
  }

  const lastPoint = points[points.length - 1]!;
  ctx.lineTo(lastPoint.x, lastPoint.y);
  ctx.stroke();
}

function fillRouteEndpointCaps(ctx: CanvasRenderingContext2D, route: RenderedConnectionRoute, lineWidth: number): void {
  const firstSegment = route.visualSegments[0];
  const lastSegment = route.visualSegments[route.visualSegments.length - 1];
  if (!firstSegment || !lastSegment) {
    return;
  }

  fillConnectionEndpointCap(ctx, { x: firstSegment.x1, y: firstSegment.y1 }, route.startSide, lineWidth);
  fillConnectionEndpointCap(ctx, { x: lastSegment.x2, y: lastSegment.y2 }, route.side, lineWidth);
}

function fillConnectionEndpointCap(ctx: CanvasRenderingContext2D, point: Point, side: Side, lineWidth: number): void {
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
  ctx.lineTo(point.x + perpendicular.x * halfWidth, point.y + perpendicular.y * halfWidth);
  ctx.lineTo(point.x - perpendicular.x * halfWidth, point.y - perpendicular.y * halfWidth);
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

function pointsAreCollinear(previous: Point, current: Point, next: Point): boolean {
  return (previous.x === current.x && current.x === next.x) ||
    (previous.y === current.y && current.y === next.y);
}

function distanceBetweenPoints(start: Point, end: Point): number {
  return Math.abs(end.x - start.x) + Math.abs(end.y - start.y);
}

function pointToward(from: Point, to: Point, distance: number): Point {
  const totalDistance = distanceBetweenPoints(from, to);
  if (totalDistance === 0) {
    return { ...from };
  }

  const ratio = distance / totalDistance;
  return {
    x: from.x + (to.x - from.x) * ratio,
    y: from.y + (to.y - from.y) * ratio,
  };
}

function strokeSegments(ctx: CanvasRenderingContext2D, segments: readonly Segment[]): void {
  for (const segment of segments) {
    ctx.beginPath();
    ctx.moveTo(segment.x1, segment.y1);
    ctx.lineTo(segment.x2, segment.y2);
    ctx.stroke();
  }
}

function boundsContainPoint(bounds: CanvasBounds, x: number, y: number, padding = 0): boolean {
  return (
    x >= bounds.left - padding &&
    x <= bounds.right + padding &&
    y >= bounds.top - padding &&
    y <= bounds.bottom + padding
  );
}

function addConnectedNodeId(map: Map<number, Set<number>>, sourceId: number, targetId: number): void {
  const existing = map.get(sourceId);
  if (existing) {
    existing.add(targetId);
    return;
  }

  map.set(sourceId, new Set([targetId]));
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

const EMPTY_NODE_ID_SET = new Set<number>();
const EMPTY_SECTION_KEY_SET = new Set<string>();
const DEFAULT_CONNECTION_LANE: CanvasConnectionLane = {
  offset: 0,
  index: 0,
  groupSize: 1,
};
const DEFAULT_CONNECTION_SHADE: CanvasConnectionShade = {
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
