import { CanvasConnectionRenderOptions } from './canvas-connection-renderer';
import { CanvasHitTester } from './canvas-hit-tester';
import {
  CanvasDragConnectionState,
  CanvasInteractionState,
  CanvasMarqueeState,
} from './canvas-interaction-controller';
import { CanvasNodeRenderOptions } from './canvas-node-renderer';
import { CanvasModeCapabilities } from './canvas-runtime';
import {
  CanvasConnectionPresentation,
  CanvasRenderSnapshot,
  RenderedNode,
} from './canvas-render-model';
import { CanvasNodeIssueSummary } from './canvas-node-render-types';
import {
  CanvasResolvedValidationFocus,
  CanvasValidationFocusResolver,
} from './canvas-validation-focus-resolver';
import { NodePortDefinition, Point, Side } from './node-model';
import { PathfinderObstacle } from './pathfinder';

export interface CanvasRenderFrameRequest {
  readonly snapshot: CanvasRenderSnapshot;
  readonly capabilities: CanvasModeCapabilities;
  readonly connectionPresentation: CanvasConnectionPresentation;
  readonly interaction: CanvasInteractionState;
  readonly readonlySelectedNodeId: number | null;
  readonly selectedNodeIds: ReadonlySet<number>;
  readonly selectedConnectionIds: ReadonlySet<string>;
  readonly validationFocus: CanvasResolvedValidationFocus | null;
  readonly nodeIssueSummaries: ReadonlyMap<number, CanvasNodeIssueSummary>;
}

export interface CanvasConnectionPreviewPlan {
  readonly sx: number;
  readonly sy: number;
  readonly ex: number;
  readonly ey: number;
  readonly obstacles: readonly PathfinderObstacle[];
  readonly side?: Side;
  readonly destNode?: PathfinderObstacle;
  readonly startSide?: Side;
  readonly startNode?: PathfinderObstacle;
}

export interface CanvasHoverTooltipPlan {
  readonly label: string;
  readonly point: Point;
}

export interface CanvasRenderFramePlan {
  readonly connectionLayer: {
    readonly selectedConnectionIds: ReadonlySet<string>;
    readonly options: CanvasConnectionRenderOptions;
  };
  readonly nodeLayer: {
    readonly selectedNodeIds: ReadonlySet<number>;
    readonly options: CanvasNodeRenderOptions;
  };
  readonly connectionPreview: CanvasConnectionPreviewPlan | null;
  readonly marqueeBounds: { readonly left: number; readonly top: number; readonly right: number; readonly bottom: number } | null;
  readonly failureTooltip: { readonly node: RenderedNode; readonly message: string } | null;
  readonly hoverTooltip: CanvasHoverTooltipPlan | null;
}

export class CanvasRenderFrameFactory {
  constructor(
    private readonly hitTester = new CanvasHitTester(),
    private readonly validationResolver = new CanvasValidationFocusResolver(),
  ) {}

  createFramePlan(request: CanvasRenderFrameRequest): CanvasRenderFramePlan {
    const { snapshot, interaction, capabilities } = request;
    const selectedNodeIds = capabilities.readOnly
      ? new Set<number>(request.readonlySelectedNodeId !== null ? [request.readonlySelectedNodeId] : [])
      : request.selectedNodeIds;
    const selectedConnectionIds = capabilities.readOnly
      ? new Set<string>()
      : request.selectedConnectionIds;
    const channelFocusedConnectionIds = this.hitTester.channelFocusedConnectionIds(
      snapshot,
      interaction.nodeId,
      interaction.portKey,
    );
    const focusedPortKeys = this.validationResolver.focusedEndpointPortKeys(
      snapshot,
      selectedConnectionIds,
      interaction.connectionRoute,
      interaction.connectionId,
      channelFocusedConnectionIds,
      request.validationFocus,
    );
    const drag = interaction.dragConnection;

    return {
      connectionLayer: {
        selectedConnectionIds,
        options: {
          readOnly: capabilities.readOnly,
          detailTier: snapshot.detailTier,
          connectionPresentation: request.connectionPresentation,
          hoveredConnectionId: interaction.connectionId,
          hoveredConnectionRouteIndex: interaction.connectionRoute?.routeIndex ?? null,
          channelFocusedConnectionIds,
          validationConnectionFocus: request.validationFocus?.connectionId
            ? {
              connectionId: request.validationFocus.connectionId,
              routeIndex: request.validationFocus.routeIndex,
              severity: request.validationFocus.severity,
            }
            : null,
        },
      },
      nodeLayer: {
        selectedNodeIds,
        options: {
          readOnly: capabilities.readOnly,
          detailTier: snapshot.detailTier,
          blockedTargetNodeIds: drag
            ? this.hitTester.blockedTargetNodeIds(snapshot, drag)
            : undefined,
          collectionIteratorPreviewLabels: drag
            ? this.hitTester.collectionIteratorPreviewLabels(snapshot, drag)
            : undefined,
          focusedPortKeys,
          nodeIssueSummaries: request.nodeIssueSummaries,
          passiveSummaryPorts: capabilities.passiveSummaryPorts,
          validationFocus: request.validationFocus
            ? {
              severity: request.validationFocus.severity,
              nodeId: request.validationFocus.nodeId,
              sectionKey: request.validationFocus.sectionKey,
              portKeys: request.validationFocus.portKeys,
            }
            : null,
        },
      },
      connectionPreview: this.connectionPreview(snapshot, capabilities, interaction),
      marqueeBounds: this.marqueeBounds(interaction.marquee),
      failureTooltip: this.failureTooltip(snapshot, interaction.nodeId),
      hoverTooltip: this.hoverTooltip(snapshot, capabilities, interaction, request.nodeIssueSummaries),
    };
  }

  private connectionPreview(
    snapshot: CanvasRenderSnapshot,
    capabilities: CanvasModeCapabilities,
    interaction: CanvasInteractionState,
  ): CanvasConnectionPreviewPlan | null {
    const drag = interaction.dragConnection;
    if (!drag || !capabilities.dragConnectionPreview) {
      return null;
    }

    const fromNode = snapshot.nodeById.get(drag.from);
    if (!fromNode || fromNode.contains(drag.x, drag.y)) {
      return null;
    }

    const startPort = drag.portKey ? fromNode.portDefinition(drag.portKey) : fromNode.outputSummaryPort(drag.anchor);
    if (!startPort) {
      return null;
    }

    const hoveredNode = interaction.nodeId && interaction.nodeId !== drag.from
      ? snapshot.nodeById.get(interaction.nodeId)
      : undefined;
    const obstacles = snapshot.nodes.filter((node) => node.id !== fromNode.id && node.id !== hoveredNode?.id);
    const startSide = fromNode.anchorDefinition(drag.anchor)?.side;

    return {
      sx: startPort.x,
      sy: startPort.y,
      ex: drag.x,
      ey: drag.y,
      obstacles,
      side: drag.side ?? startSide,
      destNode: hoveredNode,
      startSide,
      startNode: fromNode,
    };
  }

  private marqueeBounds(marquee: CanvasMarqueeState | null): CanvasRenderFramePlan['marqueeBounds'] {
    if (!marquee) {
      return null;
    }

    return {
      left: Math.min(marquee.startWorld.x, marquee.currentWorld.x),
      top: Math.min(marquee.startWorld.y, marquee.currentWorld.y),
      right: Math.max(marquee.startWorld.x, marquee.currentWorld.x),
      bottom: Math.max(marquee.startWorld.y, marquee.currentWorld.y),
    };
  }

  private failureTooltip(
    snapshot: CanvasRenderSnapshot,
    hoveredNodeId: number | null,
  ): CanvasRenderFramePlan['failureTooltip'] {
    const hoveredNode = hoveredNodeId !== null
      ? snapshot.nodeById.get(hoveredNodeId) ?? null
      : null;
    const message = hoveredNode?.failureMessage?.trim();
    return hoveredNode?.renderStatus === 'Failed' && message
      ? { node: hoveredNode, message }
      : null;
  }

  private hoverTooltip(
    snapshot: CanvasRenderSnapshot,
    capabilities: CanvasModeCapabilities,
    interaction: CanvasInteractionState,
    nodeIssueSummaries: ReadonlyMap<number, CanvasNodeIssueSummary>,
  ): CanvasHoverTooltipPlan | null {
    if (capabilities.readOnly || interaction.dragConnection) {
      return null;
    }

    const hoveredNodeId = interaction.nodeId;
    if (hoveredNodeId === null) {
      return null;
    }

    const node = snapshot.nodeById.get(hoveredNodeId);
    if (!node) {
      return null;
    }

    if (interaction.nodeIssueNodeId === hoveredNodeId) {
      const issueSummary = nodeIssueSummaries.get(hoveredNodeId);
      if (issueSummary) {
        const rect = node.settingsRect();
        return {
          label: issueSummary.tooltip,
          point: {
            x: rect.x + rect.width / 2,
            y: rect.y,
          },
        };
      }
    }

    if (interaction.portKey) {
      const port = node.portDefinition(interaction.portKey);
      if (port) {
        return {
          label: this.portTooltipLabel(port),
          point: {
            x: port.x + (port.side === 'left' ? -10 : 10),
            y: port.y - 4,
          },
        };
      }
    }

    const section = interaction.sectionKey ? this.sectionByKey(node, interaction.sectionKey) : null;
    if (!section) {
      return null;
    }

    return {
      label: `${section.expanded ? 'Collapse' : 'Expand'} ${section.label} properties`,
      point: {
        x: section.toggleRect.x + section.toggleRect.width / 2,
        y: section.toggleRect.y,
      },
    };
  }

  private portTooltipLabel(port: NodePortDefinition): string {
    const name = this.humanizePortLabel(port.propertyName ?? port.channelName ?? 'port');
    return port.side === 'left'
      ? `Accepts incoming ${name}`
      : `Connect from ${name}`;
  }

  private sectionByKey(node: RenderedNode, sectionKey: string) {
    const sections = node.layout.inputSection
      ? [node.layout.inputSection, ...node.layout.outputSections]
      : node.layout.outputSections;
    return sections.find((section) => section.key === sectionKey) ?? null;
  }

  private humanizePortLabel(value: string): string {
    return value
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .replace(/[_-]+/g, ' ')
      .trim()
      .toLowerCase() || 'port';
  }
}

export type { CanvasDragConnectionState };
