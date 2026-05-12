import { CanvasBounds, CanvasRenderSnapshot, CanvasValidationSeverity, RenderedNode } from './canvas-render-model';
import { CanvasNodeIssueSummary, CanvasPropertyIssueSummary } from './canvas-node-render-types';
import { CanvasHoveredConnectionRouteState } from './canvas-hit-tester';
import { NodeChannelSectionLayout, NodePortDefinition, Side } from './node-model';

export interface CanvasValidationFocus {
  readonly issueId: string;
  readonly severity: CanvasValidationSeverity;
  readonly nodeId?: number | null;
  readonly connectionId?: string | null;
  readonly routeIndex?: number | null;
  readonly propertyName?: string | null;
  readonly channelName?: string | null;
  readonly side?: Side | null;
  readonly pulseKey: number;
}

export interface CanvasValidationIssueMarker {
  readonly issueId: string;
  readonly severity: CanvasValidationSeverity;
  readonly nodeId: number;
  readonly message: string;
  readonly propertyName?: string | null;
  readonly channelName?: string | null;
  readonly side?: Side | null;
}

export interface CanvasResolvedValidationFocus {
  readonly issueId: string;
  readonly severity: CanvasValidationSeverity;
  readonly nodeId: number | null;
  readonly connectionId: string | null;
  readonly routeIndex: number | null;
  readonly sectionKey: string | null;
  readonly portKeys: ReadonlySet<string>;
  readonly bounds: CanvasBounds | null;
}

export interface CanvasValidationFocusRequest {
  readonly snapshot: CanvasRenderSnapshot;
  readonly focus: CanvasValidationFocus | null;
}

export class CanvasValidationFocusResolver {
  buildNodeIssueSummaries(markers: readonly CanvasValidationIssueMarker[]): ReadonlyMap<number, CanvasNodeIssueSummary> {
    const grouped = new Map<number, {
      errorCount: number;
      warningCount: number;
      messages: string[];
      propertyIssues: Map<string, CanvasPropertyIssueSummary>;
    }>();

    for (const marker of markers) {
      const message = marker.message.trim();
      const group = grouped.get(marker.nodeId) ?? {
        errorCount: 0,
        warningCount: 0,
        messages: [],
        propertyIssues: new Map<string, CanvasPropertyIssueSummary>(),
      };

      if (marker.severity === 'error') {
        group.errorCount += 1;
      } else {
        group.warningCount += 1;
      }

      if (message.length > 0) {
        group.messages.push(message);
      }

      if (marker.propertyName && marker.channelName) {
        const key = this.validationMarkerPropertyKey(marker);
        const existingPropertyIssue = group.propertyIssues.get(key);
        group.propertyIssues.set(key, {
          severity: existingPropertyIssue?.severity === 'error' || marker.severity === 'error'
            ? 'error'
            : 'warning',
          count: (existingPropertyIssue?.count ?? 0) + 1,
        });
      }

      grouped.set(marker.nodeId, group);
    }

    return new Map([...grouped.entries()].map(([nodeId, group]) => {
      const severity: CanvasValidationSeverity = group.errorCount > 0 ? 'error' : 'warning';
      return [nodeId, {
        severity,
        errorCount: group.errorCount,
        warningCount: group.warningCount,
        tooltip: this.issueSummaryTooltip(group.errorCount, group.warningCount, group.messages),
        propertyIssues: group.propertyIssues,
      }] as const;
    }));
  }

  resolveValidationFocus(request: CanvasValidationFocusRequest): CanvasResolvedValidationFocus | null {
    const { snapshot, focus } = request;
    if (!focus) {
      return null;
    }

    const portKeys = new Set<string>();
    let bounds: CanvasBounds | null = null;
    let nodeId = focus.nodeId ?? null;
    let connectionId = focus.connectionId ?? null;
    let routeIndex = focus.routeIndex ?? null;
    let sectionKey: string | null = null;

    if (connectionId) {
      const connection = snapshot.connectionById.get(connectionId);
      if (connection) {
        const route = routeIndex !== null ? connection.routes[routeIndex] ?? null : null;
        bounds = this.unionCanvasBounds(bounds, route?.bounds ?? connection.routeBounds);
        if (route) {
          portKeys.add(this.portFocusKey(connection.from, route.startPortKey));
          portKeys.add(this.portFocusKey(connection.to, route.endPortKey));
        } else {
          const sourceSummaryPort = connection.from.outputSummaryPort(connection.anchor);
          const targetSummaryPort = connection.to.inputSummaryPort();
          if (sourceSummaryPort) {
            portKeys.add(this.portFocusKey(connection.from, sourceSummaryPort.key));
          }
          if (targetSummaryPort) {
            portKeys.add(this.portFocusKey(connection.to, targetSummaryPort.key));
          }
        }
      } else {
        connectionId = null;
        routeIndex = null;
      }
    }

    if (nodeId !== null) {
      const node = snapshot.nodeById.get(nodeId);
      if (node) {
        const nodeTarget = this.resolveValidationNodeTarget(node, focus);
        bounds = this.unionCanvasBounds(bounds, nodeTarget.bounds);
        sectionKey = nodeTarget.sectionKey;
        for (const portKey of nodeTarget.portKeys) {
          portKeys.add(portKey);
        }
      } else {
        nodeId = null;
      }
    }

    if (nodeId === null && connectionId === null) {
      return null;
    }

    return {
      issueId: focus.issueId,
      severity: focus.severity,
      nodeId,
      connectionId,
      routeIndex,
      sectionKey,
      portKeys,
      bounds,
    };
  }

  sectionKeyForValidationFocus(node: RenderedNode, focus: CanvasValidationFocus): string | null {
    if (!focus.channelName) {
      return null;
    }

    if (focus.side === 'left' || (!focus.side && node.inputChannelName === focus.channelName)) {
      return node.inputChannelName === focus.channelName ? `left:${focus.channelName}` : null;
    }

    if (focus.side === 'right' || !focus.side) {
      return node.outputChannelNames.includes(focus.channelName) ? `right:${focus.channelName}` : null;
    }

    return null;
  }

  focusedEndpointPortKeys(
    snapshot: CanvasRenderSnapshot,
    selectedConnectionIds: ReadonlySet<string>,
    hoveredConnectionRoute: CanvasHoveredConnectionRouteState | null,
    hoveredConnectionId: string | null,
    channelFocusedConnectionIds: ReadonlySet<string> = new Set<string>(),
    validationFocus: CanvasResolvedValidationFocus | null = null,
  ): ReadonlySet<string> {
    const focusedConnectionIds = new Set(
      [...selectedConnectionIds].filter((connectionId) => connectionId !== hoveredConnectionRoute?.connectionId),
    );
    if (hoveredConnectionId && !hoveredConnectionRoute) {
      focusedConnectionIds.add(hoveredConnectionId);
    }
    for (const connectionId of channelFocusedConnectionIds) {
      focusedConnectionIds.add(connectionId);
    }
    if (validationFocus?.connectionId) {
      focusedConnectionIds.add(validationFocus.connectionId);
    }

    if (focusedConnectionIds.size === 0 && !validationFocus) {
      return new Set();
    }

    const portKeys = new Set(validationFocus?.portKeys ?? []);
    if (hoveredConnectionRoute) {
      this.addRouteEndpointPortKeys(snapshot, hoveredConnectionRoute, portKeys);
    }

    for (const connectionId of focusedConnectionIds) {
      const connection = snapshot.connectionById.get(connectionId);
      if (!connection) {
        continue;
      }

      const sourceSummaryPort = connection.from.outputSummaryPort(connection.anchor);
      if (sourceSummaryPort) {
        portKeys.add(this.portFocusKey(connection.from, sourceSummaryPort.key));
      }

      const targetSummaryPort = connection.to.inputSummaryPort();
      if (targetSummaryPort) {
        portKeys.add(this.portFocusKey(connection.to, targetSummaryPort.key));
      }

      for (const mapping of connection.effectiveMappings) {
        const sourcePropertyPort = connection.from.outputPropertyPort(connection.anchor, mapping.sourceProperty);
        if (sourcePropertyPort) {
          portKeys.add(this.portFocusKey(connection.from, sourcePropertyPort.key));
        }

        const targetPropertyPort = connection.to.inputPropertyPort(mapping.targetProperty);
        if (targetPropertyPort) {
          portKeys.add(this.portFocusKey(connection.to, targetPropertyPort.key));
        }
      }
    }

    return portKeys;
  }

  portFocusKey(node: RenderedNode, portKey: string): string {
    return `${node.id}:${portKey}`;
  }

  private resolveValidationNodeTarget(
    node: RenderedNode,
    focus: CanvasValidationFocus,
  ): { bounds: CanvasBounds | null; sectionKey: string | null; portKeys: ReadonlySet<string> } {
    const portKeys = new Set<string>();
    const section = this.sectionForValidationFocus(node, focus);
    if (!section) {
      return {
        bounds: this.nodeBounds(node),
        sectionKey: null,
        portKeys,
      };
    }

    let bounds: CanvasBounds | null = this.sectionBounds(node, section);
    const targetRow = focus.propertyName
      ? section.propertyRows.find((row) => row.name === focus.propertyName) ?? null
      : null;

    if (targetRow) {
      bounds = this.unionCanvasBounds(bounds, this.propertyRowBounds(node, targetRow));
      portKeys.add(this.portFocusKey(node, targetRow.port.key));
    } else {
      portKeys.add(this.portFocusKey(node, section.summaryPort.key));
    }

    return {
      bounds,
      sectionKey: section.key,
      portKeys,
    };
  }

  private sectionForValidationFocus(node: RenderedNode, focus: CanvasValidationFocus): NodeChannelSectionLayout | null {
    const sectionKey = this.sectionKeyForValidationFocus(node, focus);
    if (!sectionKey) {
      return null;
    }

    if (node.layout.inputSection?.key === sectionKey) {
      return node.layout.inputSection;
    }

    return node.layout.outputSections.find((section) => section.key === sectionKey) ?? null;
  }

  private nodeBounds(node: RenderedNode): CanvasBounds {
    return {
      left: node.x,
      top: node.y,
      right: node.x + node.width,
      bottom: node.y + node.height,
    };
  }

  private sectionBounds(node: RenderedNode, section: NodeChannelSectionLayout): CanvasBounds {
    const portBounds = this.portBounds(section.summaryPort);
    return this.unionCanvasBounds(
      {
        left: node.x + 14,
        top: section.headerTop,
        right: node.x + node.width - 14,
        bottom: section.bottom,
      },
      portBounds,
    )!;
  }

  private propertyRowBounds(
    node: RenderedNode,
    row: NodeChannelSectionLayout['propertyRows'][number],
  ): CanvasBounds {
    return this.unionCanvasBounds(
      {
        left: node.x + 18,
        top: row.top,
        right: node.x + node.width - 18,
        bottom: row.top + row.height,
      },
      this.portBounds(row.port),
    )!;
  }

  private portBounds(port: NodePortDefinition): CanvasBounds {
    const rect = port.hitBounds ?? port.visualBounds;
    if (rect) {
      return {
        left: rect.x,
        top: rect.y,
        right: rect.x + rect.width,
        bottom: rect.y + rect.height,
      };
    }

    return {
      left: port.x - port.radius,
      top: port.y - port.radius,
      right: port.x + port.radius,
      bottom: port.y + port.radius,
    };
  }

  private unionCanvasBounds(left: CanvasBounds | null, right: CanvasBounds | null): CanvasBounds | null {
    if (!left) {
      return right;
    }

    if (!right) {
      return left;
    }

    return {
      left: Math.min(left.left, right.left),
      top: Math.min(left.top, right.top),
      right: Math.max(left.right, right.right),
      bottom: Math.max(left.bottom, right.bottom),
    };
  }

  private addRouteEndpointPortKeys(
    snapshot: CanvasRenderSnapshot,
    hoveredConnectionRoute: CanvasHoveredConnectionRouteState,
    portKeys: Set<string>,
  ): void {
    const connection = snapshot.connectionById.get(hoveredConnectionRoute.connectionId);
    const route = connection?.routes[hoveredConnectionRoute.routeIndex];
    if (!connection || !route) {
      return;
    }

    portKeys.add(this.portFocusKey(connection.from, route.startPortKey));
    portKeys.add(this.portFocusKey(connection.to, route.endPortKey));
  }

  private issueSummaryTooltip(errorCount: number, warningCount: number, messages: readonly string[]): string {
    if (errorCount + warningCount === 1) {
      return messages[0] ?? (errorCount === 1 ? '1 error' : '1 warning');
    }

    const parts: string[] = [];
    if (errorCount > 0) {
      parts.push(`${errorCount} ${errorCount === 1 ? 'error' : 'errors'}`);
    }

    if (warningCount > 0) {
      parts.push(`${warningCount} ${warningCount === 1 ? 'warning' : 'warnings'}`);
    }

    return parts.join(' · ');
  }

  private validationMarkerPropertyKey(marker: CanvasValidationIssueMarker): string {
    return `${marker.side ?? ''}:${marker.channelName ?? ''}:${marker.propertyName ?? ''}`;
  }
}
