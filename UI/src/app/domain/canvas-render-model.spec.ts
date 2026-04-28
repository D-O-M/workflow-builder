import { describe, expect, it } from 'vitest';
import { LANE_CENTER_SPACING, buildCanvasRenderSnapshot } from './canvas-render-model';
import { ConnectionModel } from './connection-model';
import { NodeModel, NodeRenderStatus } from './node-model';
import { PathfinderOptions } from './pathfinder';
import { createCanvasPerformanceFixture } from './canvas-performance-fixtures';

const opts: PathfinderOptions = { gridSize: 10, nodeMargin: 20, obstacleBuffer: 2 };

function createNode(
  id: number,
  name: string,
  x: number,
  y: number,
  options?: {
    width?: number;
    height?: number;
    renderStatus?: NodeRenderStatus | null;
    inputProperties?: Record<string, 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Guid' | 'Date' | 'DateTime' | 'Enum'>;
    outputProperties?: Record<string, Record<string, 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Guid' | 'Date' | 'DateTime' | 'Enum'>>;
  },
): NodeModel {
  return new NodeModel(
    id,
    id,
    0,
    0,
    id * 10,
    '0.0.1',
    name,
    x,
    y,
    options?.width ?? 320,
    options?.height ?? 220,
    options?.inputProperties ? 'Input' : null,
    Object.keys(options?.outputProperties ?? {}),
    false,
    options?.renderStatus ?? null,
    null,
    options?.inputProperties ?? {},
    options?.outputProperties ?? {},
  );
}

function routeTurnCount(segments: readonly { x1: number; y1: number; x2: number; y2: number }[]): number {
  let turns = 0;
  let previousAxis: 'horizontal' | 'vertical' | null = null;

  for (const segment of segments) {
    const axis = segment.y1 === segment.y2 ? 'horizontal' : 'vertical';
    if (previousAxis && previousAxis !== axis) {
      turns += 1;
    }

    previousAxis = axis;
  }

  return turns;
}

function routeHasRepeatedPoint(segments: readonly { x1: number; y1: number; x2: number; y2: number }[]): boolean {
  if (segments.length === 0) {
    return false;
  }

  const points = [
    { x: segments[0]!.x1, y: segments[0]!.y1 },
    ...segments.map((segment) => ({ x: segment.x2, y: segment.y2 })),
  ];
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

function routeHasOppositeConsecutiveSegments(
  segments: readonly { x1: number; y1: number; x2: number; y2: number }[],
): boolean {
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

describe('NodeModel layout caching', () => {
  it('reuses cached layouts for the same expansion state', () => {
    const node = createNode(1, 'Lookup', 0, 0, {
      inputProperties: { town: 'String' },
      outputProperties: {
        Success: { town: 'String', score: 'Integer' },
      },
    });

    expect(node.layoutForState(false)).toBe(node.layoutForState(false));
    expect(node.layoutForState(true)).toBe(node.layoutForState(true));
    expect(node.layoutForState(false)).not.toBe(node.layoutForState(true));
  });

  it('places channel summary docks outside the node while keeping them aligned to their channels', () => {
    const node = createNode(1, 'Join', 100, 80, {
      inputProperties: { town: 'String' },
      outputProperties: {
        Success: { score: 'Integer' },
        Failure: { reason: 'String' },
      },
    });
    const layout = node.layoutForState(false);
    const inputSection = layout.inputSection!;
    const firstOutput = layout.outputSections[0]!;
    const secondOutput = layout.outputSections[1]!;

    expect(inputSection.summaryPort.x).toBeLessThan(node.x);
    expect(inputSection.summaryPort.visualBounds!.x).toBeLessThan(node.x);
    expect(inputSection.summaryPort.y).toBe(inputSection.headerTop + inputSection.headerHeight / 2);

    expect(firstOutput.summaryPort.x).toBeGreaterThan(node.x + node.width);
    expect(firstOutput.summaryPort.visualBounds!.x).toBeLessThan(node.x + node.width);
    expect(firstOutput.summaryPort.visualBounds!.x + firstOutput.summaryPort.visualBounds!.width).toBeGreaterThan(node.x + node.width);
    expect(firstOutput.summaryPort.y).toBe(firstOutput.headerTop + firstOutput.headerHeight / 2);
    expect(secondOutput.summaryPort.y).not.toBe(firstOutput.summaryPort.y);
  });

  it('centers expanded property sockets over the node edge', () => {
    const node = createNode(1, 'Join', 100, 80, {
      inputProperties: { town: 'String' },
      outputProperties: {
        Success: { score: 'Integer' },
      },
    });
    const expandedLayout = node.layoutForExpandedSections(new Set(['left:Input', 'right:Success']));
    const inputPropertyPort = expandedLayout.inputSection!.propertyRows[0]!.port;
    const outputPropertyPort = expandedLayout.outputSections[0]!.propertyRows[0]!.port;

    expect(inputPropertyPort.x).toBe(node.x);
    expect(outputPropertyPort.x).toBe(node.x + node.width);
  });
});

describe('buildCanvasRenderSnapshot', () => {
  it('keeps property-level routes in full detail and groups them in simplified detail', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
          score: 'Integer',
        },
      },
    });
    const destination = createNode(2, 'Destination', 420, 0, {
      inputProperties: {
        town: 'String',
        score: 'Integer',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'score', targetProperty: 'score' },
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const expandedSections = new Map<number, ReadonlySet<string>>([
      [1, new Set(['right:Success'])],
      [2, new Set(['left:Input'])],
    ]);
    const fullSnapshot = buildCanvasRenderSnapshot([source, destination], [connection], expandedSections, 'full', opts, false);
    const simplifiedSnapshot = buildCanvasRenderSnapshot([source, destination], [connection], expandedSections, 'simplified', opts, false);

    expect(fullSnapshot.connections[0]?.routes.length).toBe(2);
    expect(simplifiedSnapshot.connections[0]?.routes.length).toBe(1);
  });

  it('expands input and output channel sections independently', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
          score: 'Integer',
        },
      },
    });
    const destination = createNode(2, 'Destination', 420, 0, {
      inputProperties: {
        town: 'String',
        score: 'Integer',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'score', targetProperty: 'score' },
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const inputOnlySnapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [connection],
      new Map([[2, new Set(['left:Input'])]]),
      'full',
      opts,
      false,
    );
    const outputOnlySnapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [connection],
      new Map([[1, new Set(['right:Success'])]]),
      'full',
      opts,
      false,
    );

    expect(inputOnlySnapshot.nodeById.get(1)?.layout.outputSections[0]?.propertyRows.length).toBe(0);
    expect(inputOnlySnapshot.nodeById.get(2)?.layout.inputSection?.propertyRows.length).toBe(2);
    expect(inputOnlySnapshot.connections[0]?.routes.length).toBe(2);

    expect(outputOnlySnapshot.nodeById.get(1)?.layout.outputSections[0]?.propertyRows.length).toBe(2);
    expect(outputOnlySnapshot.nodeById.get(2)?.layout.inputSection?.propertyRows.length).toBe(0);
    expect(outputOnlySnapshot.connections[0]?.routes.length).toBe(2);
  });

  it('precomputes connectivity and spatial queries from the snapshot', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 420, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [connection],
      new Map([
        [1, new Set(['right:Success'])],
        [2, new Set(['left:Input'])],
      ]),
      'full',
      opts,
      false,
    );
    const renderedSource = snapshot.nodeById.get(1)!;
    const renderedConnection = snapshot.connections[0]!;
    const firstSegment = renderedConnection.routes[0]!.segments[0]!;
    const connectionPoint = {
      x: (firstSegment.x1 + firstSegment.x2) / 2,
      y: (firstSegment.y1 + firstSegment.y2) / 2,
    };

    expect(snapshot.getConnectedNodes(1).map((node) => node.id)).toEqual([2]);
    expect(snapshot.queryNodesAtPoint(renderedSource.x + 10, renderedSource.y + 10).map((node) => node.id)).toContain(1);
    expect(snapshot.queryConnectionsAtPoint(connectionPoint.x, connectionPoint.y).map((candidate) => candidate.id)).toContain('connection-1');
    expect(snapshot.portConnectivity.summaryPortKeys.size).toBeGreaterThan(0);
    expect(snapshot.portConnectivity.propertyPortKeys.size).toBeGreaterThan(0);
  });

  it('scopes port connectivity to the owning node so matching channel names do not cross-highlight', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const connectedDestination = createNode(2, 'Connected Destination', 420, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const unrelatedDestination = createNode(3, 'Unrelated Destination', 420, 180, {
      inputProperties: {
        town: 'String',
      },
    });
    const connection = new ConnectionModel(source, connectedDestination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, connectedDestination, unrelatedDestination],
      [connection],
      new Map([[2, new Set(['left:Input'])], [3, new Set(['left:Input'])]]),
      'full',
      opts,
      false,
    );

    expect(snapshot.portConnectivity.summaryPortKeys.has('2:left:Input:summary')).toBe(true);
    expect(snapshot.portConnectivity.summaryPortKeys.has('3:left:Input:summary')).toBe(false);
    expect(snapshot.portConnectivity.propertyPortKeys.has('2:left:Input:town')).toBe(true);
    expect(snapshot.portConnectivity.propertyPortKeys.has('3:left:Input:town')).toBe(false);
  });

  it('indexes and hit-tests exterior channel docks as node interaction areas', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const snapshot = buildCanvasRenderSnapshot([source], [], new Map(), 'full', opts, false);
    const renderedSource = snapshot.nodeById.get(1)!;
    const dock = renderedSource.layout.outputSections[0]!.summaryPort;

    expect(renderedSource.contains(dock.x, dock.y)).toBe(false);
    expect(renderedSource.containsInteractionPoint(dock.x, dock.y)).toBe(true);
    expect(renderedSource.portHit(dock.x, dock.y, 0)?.key).toBe(dock.key);
    expect(snapshot.queryNodesAtPoint(dock.x, dock.y).map((node) => node.id)).toContain(1);
  });

  it('routes connections to the exterior dock coordinates', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot([source, destination], [connection], new Map(), 'full', opts, false);
    const renderedSource = snapshot.nodeById.get(1)!;
    const renderedDestination = snapshot.nodeById.get(2)!;
    const sourceDock = renderedSource.outputSummaryPort(0)!;
    const destinationDock = renderedDestination.inputSummaryPort()!;
    const route = snapshot.connectionById.get('connection-1')!.routes[0]!;
    const first = route.segments[0]!;
    const last = route.segments[route.segments.length - 1]!;

    expect(sourceDock.x).toBeGreaterThan(renderedSource.x + renderedSource.width);
    expect(destinationDock.x).toBeLessThan(renderedDestination.x);
    expect(first.x1).toBe(sourceDock.x);
    expect(first.y1).toBe(sourceDock.y);
    expect(last.x2).toBe(destinationDock.x);
    expect(last.y2).toBe(destinationDock.y);
    expect(route.startSide).toBe('right');
    expect(route.side).toBe('left');
    expect(route.status).toBe('clear');
  });

  it('assigns deterministic visual lanes to stacked sibling connections', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const first = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [first, second],
      new Map(),
      'full',
      opts,
      false,
    );

    const firstRoute = snapshot.connectionById.get('connection-a')!.routes[0]!;
    const secondRoute = snapshot.connectionById.get('connection-b')!.routes[0]!;

    expect(firstRoute.lane.offset).toBeCloseTo(-LANE_CENTER_SPACING / 2);
    expect(secondRoute.lane.offset).toBeCloseTo(LANE_CENTER_SPACING / 2);
    expect(secondRoute.lane.offset - firstRoute.lane.offset).toBeCloseTo(LANE_CENTER_SPACING);
    expect(firstRoute.visualSegments).not.toEqual(firstRoute.segments);
    expect(secondRoute.visualSegments).not.toEqual(secondRoute.segments);
  });

  it('assigns complementary source hue groups with monochrome variants inside each source node', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          score: 'Integer',
          town: 'String',
        },
      },
    });
    const otherSource = createNode(2, 'Other Source', 0, 260, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const firstDestination = createNode(3, 'First Destination', 520, -80, {
      inputProperties: {
        score: 'Integer',
        town: 'String',
      },
    });
    const secondDestination = createNode(4, 'Second Destination', 520, 180, {
      inputProperties: {
        town: 'String',
      },
    });
    const mapped = new ConnectionModel(source, firstDestination, 0, 'left', 62, 'mapped', [
      { sourceProperty: 'score', targetProperty: 'score' },
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const fanOut = new ConnectionModel(source, secondDestination, 0, 'left', 62, 'fan-out', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const other = new ConnectionModel(otherSource, secondDestination, 0, 'left', 62, 'other', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, otherSource, firstDestination, secondDestination],
      [mapped, fanOut, other],
      new Map([
        [1, new Set(['right:Success'])],
        [3, new Set(['left:Input'])],
      ]),
      'full',
      opts,
      false,
    );

    expect(snapshot.connectionById.get('mapped')!.routes.map((route) => route.shade.index)).toEqual([0, 1]);
    expect(snapshot.connectionById.get('fan-out')!.routes.map((route) => route.shade.index)).toEqual([2]);
    expect(snapshot.connectionById.get('other')!.routes.map((route) => route.shade.index)).toEqual([3]);
    expect(snapshot.connections.flatMap((connection) => connection.routes).map((route) => route.shade.total)).toEqual([4, 4, 4, 4]);

    expect(snapshot.connectionById.get('mapped')!.routes.map((route) => route.shade.sourceIndex)).toEqual([0, 0]);
    expect(snapshot.connectionById.get('mapped')!.routes.map((route) => route.shade.variantIndex)).toEqual([0, 1]);
    expect(snapshot.connectionById.get('mapped')!.routes.map((route) => route.sourceProperty)).toEqual(['score', 'town']);
    expect(snapshot.connectionById.get('mapped')!.routes.map((route) => route.targetProperty)).toEqual(['score', 'town']);
    expect(snapshot.connectionById.get('fan-out')!.routes.map((route) => route.shade.sourceIndex)).toEqual([0]);
    expect(snapshot.connectionById.get('fan-out')!.routes.map((route) => route.shade.variantIndex)).toEqual([2]);
    expect(snapshot.connectionById.get('other')!.routes.map((route) => route.shade.sourceIndex)).toEqual([1]);
    expect(snapshot.connectionById.get('other')!.routes.map((route) => route.shade.variantIndex)).toEqual([0]);
    expect(snapshot.connectionById.get('mapped')!.routes.map((route) => route.shade.variantTotal)).toEqual([3, 3]);
    expect(snapshot.connectionById.get('other')!.routes.map((route) => route.shade.variantTotal)).toEqual([1]);
  });

  it('maps readonly process connection shades from the source step status', () => {
    const cases: readonly { sourceStatus: NodeRenderStatus | null; expected: 'inactive' | 'completed' | 'failed' }[] = [
      { sourceStatus: 'Completed', expected: 'completed' },
      { sourceStatus: 'Failed', expected: 'failed' },
      { sourceStatus: 'NotExecuted', expected: 'inactive' },
      { sourceStatus: 'Pending', expected: 'inactive' },
      { sourceStatus: 'InProgress', expected: 'inactive' },
      { sourceStatus: null, expected: 'inactive' },
    ];

    for (const { sourceStatus, expected } of cases) {
      const source = createNode(1, `Source ${sourceStatus ?? 'unknown'}`, 0, 0, {
        renderStatus: sourceStatus,
        outputProperties: {
          Success: {
            town: 'String',
          },
        },
      });
      const destination = createNode(2, 'Destination', 520, 0, {
        inputProperties: {
          town: 'String',
        },
      });
      const connection = new ConnectionModel(
        source,
        destination,
        0,
        'left',
        62,
        `connection-${sourceStatus ?? 'unknown'}`,
        [{ sourceProperty: 'town', targetProperty: 'town' }],
      );

      const snapshot = buildCanvasRenderSnapshot([source, destination], [connection], new Map(), 'full', opts, true);

      expect(snapshot.connections[0]?.routes.map((route) => route.shade.semanticStatus)).toEqual([expected]);
    }
  });

  it('keeps edit-mode builder connection shades free of process semantic status', () => {
    const source = createNode(1, 'Source', 0, 0, {
      renderStatus: 'Completed',
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot([source, destination], [connection], new Map(), 'full', opts, false);

    expect(snapshot.connections[0]?.routes.map((route) => route.shade.semanticStatus)).toEqual([undefined]);
    expect(snapshot.connections[0]?.routes.map((route) => route.shade.sourceIndex)).toEqual([0]);
    expect(snapshot.connections[0]?.routes.map((route) => route.shade.variantIndex)).toEqual([0]);
  });

  it('assigns lanes to multiple inbound connections sharing the same target input', () => {
    const leftSource = createNode(1, 'Left Source', 0, -120, {
      outputProperties: {
        Success: {
          left: 'String',
        },
      },
    });
    const rightSource = createNode(2, 'Right Source', 0, 160, {
      outputProperties: {
        Success: {
          right: 'String',
        },
      },
    });
    const join = createNode(3, 'Join', 520, 0, {
      inputProperties: {
        left: 'String',
        right: 'String',
      },
    });
    const leftConnection = new ConnectionModel(leftSource, join, 0, 'left', 62, 'left-connection', [
      { sourceProperty: 'left', targetProperty: 'left' },
    ]);
    const rightConnection = new ConnectionModel(rightSource, join, 0, 'left', 62, 'right-connection', [
      { sourceProperty: 'right', targetProperty: 'right' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [leftSource, rightSource, join],
      [leftConnection, rightConnection],
      new Map(),
      'full',
      opts,
      false,
    );

    expect(snapshot.connectionById.get('left-connection')!.routes[0]!.lane.offset).toBeCloseTo(-LANE_CENTER_SPACING / 2);
    expect(snapshot.connectionById.get('right-connection')!.routes[0]!.lane.offset).toBeCloseTo(LANE_CENTER_SPACING / 2);
  });

  it('spaces larger lane groups as adjacent centerlines with no deliberate gap', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destinations = [2, 3, 4].map((id, index) =>
      createNode(id, `Destination ${id}`, 520, (index - 1) * 160, {
        inputProperties: {
          town: 'String',
        },
      }));
    const connections = destinations.map((destination, index) =>
      new ConnectionModel(source, destination, 0, 'left', 62, `connection-${index}`, [
        { sourceProperty: 'town', targetProperty: 'town' },
      ]));

    const snapshot = buildCanvasRenderSnapshot(
      [source, ...destinations],
      connections,
      new Map(),
      'full',
      opts,
      false,
    );
    const offsets = connections
      .map((connection) => snapshot.connectionById.get(connection.id)!.routes[0]!.lane.offset)
      .sort((left, right) => left - right);

    expect(offsets).toEqual([
      -LANE_CENTER_SPACING,
      0,
      LANE_CENTER_SPACING,
    ]);
  });

  it('keeps lane-adjusted routes orthogonal and preserves anchor contact segments', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const first = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [first, second],
      new Map(),
      'full',
      opts,
      false,
    );
    const route = snapshot.connectionById.get('connection-a')!.routes[0]!;
    const canonicalFirst = route.segments[0]!;
    const visualFirst = route.visualSegments[0]!;
    const canonicalLast = route.segments[route.segments.length - 1]!;
    const visualLast = route.visualSegments[route.visualSegments.length - 1]!;

    expect(route.visualSegments.every((segment) => segment.x1 === segment.x2 || segment.y1 === segment.y2)).toBe(true);
    expect(visualFirst).toEqual(canonicalFirst);
    expect(visualLast).toEqual(canonicalLast);
    expect(routeHasRepeatedPoint(route.visualSegments)).toBe(false);
    expect(routeHasOppositeConsecutiveSegments(route.visualSegments)).toBe(false);
    expect(routeTurnCount(route.visualSegments)).toBeLessThanOrEqual(routeTurnCount(route.segments) + 4);
  });

  it('lane-adjusts stacked vertical route sections sideways as adjacent strokes', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 180, {
      inputProperties: {
        town: 'String',
      },
    });
    const first = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [first, second],
      new Map(),
      'full',
      opts,
      false,
    );
    const route = snapshot.connectionById.get('connection-a')!.routes[0]!;
    const canonicalVertical = route.segments.find((segment, index) =>
      index > 0 &&
      index < route.segments.length - 1 &&
      segment.x1 === segment.x2 &&
      segment.y1 !== segment.y2,
    )!;

    expect(canonicalVertical).toBeTruthy();
    expect(route.visualSegments.some((segment) =>
      segment.x1 === segment.x2 &&
      segment.y1 !== segment.y2 &&
      Math.abs(segment.x1 - (canonicalVertical.x1 + route.lane.offset)) < 0.001,
    )).toBe(true);
  });

  it('hit-tests the lane-adjusted visual route instead of only the canonical route', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const first = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [first, second],
      new Map(),
      'full',
      opts,
      false,
    );
    const connection = snapshot.connectionById.get('connection-a')!;
    const shiftedSegment = connection.routes[0]!.visualSegments.find((segment, index) =>
      index > 0 &&
      index < connection.routes[0]!.visualSegments.length - 1 &&
      (segment.x1 !== connection.routes[0]!.segments[index]?.x1 ||
        segment.y1 !== connection.routes[0]!.segments[index]?.y1),
    )!;
    const visualPoint = {
      x: (shiftedSegment.x1 + shiftedSegment.x2) / 2,
      y: (shiftedSegment.y1 + shiftedSegment.y2) / 2,
    };

    expect(connection.hitTest(visualPoint.x, visualPoint.y, 1)).toBe(true);
  });

  it('reports visual-route distance so callers can choose the nearest adjacent lane', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const first = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [first, second],
      new Map(),
      'full',
      opts,
      false,
    );
    const firstConnection = snapshot.connectionById.get('connection-a')!;
    const secondConnection = snapshot.connectionById.get('connection-b')!;
    const targetSegment = secondConnection.routes[0]!.visualSegments.find((segment, index) =>
      index > 0 &&
      index < secondConnection.routes[0]!.visualSegments.length - 1 &&
      (segment.x1 !== secondConnection.routes[0]!.segments[index]?.x1 ||
        segment.y1 !== secondConnection.routes[0]!.segments[index]?.y1),
    )!;
    const point = {
      x: (targetSegment.x1 + targetSegment.x2) / 2,
      y: (targetSegment.y1 + targetSegment.y2) / 2,
    };

    expect(secondConnection.distanceToVisualRoute(point.x, point.y, 1)).toBeCloseTo(0);
    expect(secondConnection.nearestVisualRoute(point.x, point.y, 1)?.routeIndex).toBe(0);
    expect(firstConnection.distanceToVisualRoute(point.x, point.y, LANE_CENTER_SPACING)).toBeGreaterThan(0);
  });

  it('keeps mapped and fan-out connection routes free of behavior badges', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const firstDestination = createNode(2, 'First Destination', 520, -120, {
      inputProperties: {
        town: 'String',
      },
    });
    const secondDestination = createNode(3, 'Second Destination', 520, 160, {
      inputProperties: {
        town: 'String',
      },
    });
    const first = new ConnectionModel(source, firstDestination, 0, 'left', 62, 'connection-a', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const second = new ConnectionModel(source, secondDestination, 0, 'left', 62, 'connection-b', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, firstDestination, secondDestination],
      [first, second],
      new Map(),
      'full',
      opts,
      false,
    );

    expect(snapshot.connections.map((connection) => connection.badgePosition)).toEqual([null, null]);
  });

  it('collects route validation errors for problem connections', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const sourceAnchor = source.outputAnchors()[0]!;
    const blocker = createNode(3, 'Blocker', sourceAnchor.x + 10, sourceAnchor.y - 25, {
      width: 45,
      height: 50,
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination, blocker],
      [connection],
      new Map(),
      'full',
      opts,
      false,
    );

    expect(snapshot.connections[0]?.routes[0]?.status).toBe('obstacleViolation');
    expect(snapshot.routeValidationErrors).toEqual([
      {
        connectionId: 'connection-1',
        routeIndex: 0,
        message: "Connection crosses another node's reserved routing buffer.",
      },
    ]);
  });

  it('does not collect route validation errors for clear connections', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        town: 'String',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);

    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [connection],
      new Map(),
      'full',
      opts,
      false,
    );

    expect(snapshot.connections[0]?.routes[0]?.status).toBe('clear');
    expect(snapshot.routeValidationErrors).toEqual([]);
  });

  it('uses buffered routing for expanded property-level routes', () => {
    const source = createNode(1, 'Source', 0, 0, {
      outputProperties: {
        Success: {
          score: 'Integer',
          town: 'String',
        },
      },
    });
    const destination = createNode(2, 'Destination', 520, 0, {
      inputProperties: {
        score: 'Integer',
        town: 'String',
      },
    });
    const connection = new ConnectionModel(source, destination, 0, 'left', 62, 'connection-1', [
      { sourceProperty: 'score', targetProperty: 'score' },
      { sourceProperty: 'town', targetProperty: 'town' },
    ]);
    const snapshot = buildCanvasRenderSnapshot(
      [source, destination],
      [connection],
      new Map([
        [1, new Set(['right:Success'])],
        [2, new Set(['left:Input'])],
      ]),
      'full',
      opts,
      false,
    );
    const route = snapshot.connections[0]!.routes[0]!;
    const first = route.segments[0]!;
    const last = route.segments[route.segments.length - 1]!;

    expect(snapshot.connections[0]?.routes.length).toBe(2);
    expect(first.x2 - first.x1).toBe(opts.nodeMargin);
    expect(first.y2).toBe(first.y1);
    expect(last.x2 - last.x1).toBe(opts.nodeMargin);
    expect(last.y2).toBe(last.y1);
    expect(snapshot.routeValidationErrors).toEqual([]);
  });

  it('builds a large synthetic graph snapshot for profiling-sized workloads', () => {
    const fixture = createCanvasPerformanceFixture(15, 12);
    const snapshot = buildCanvasRenderSnapshot(
      fixture.nodes,
      fixture.connections,
      new Map(fixture.nodes.slice(0, 24).map((node) => [node.id, new Set(['left:Input', 'right:Success'])] as const)),
      'simplified',
      opts,
      false,
    );

    expect(snapshot.nodes.length).toBe(180);
    expect(snapshot.connections.length).toBeGreaterThan(300);
    expect(snapshot.queryNodesAtPoint(10, 10).length).toBeGreaterThan(0);
  });
});
