import { ConnectionModel } from './connection-model';
import { NodeModel } from './node-model';

export interface CanvasPerformanceFixture {
  readonly nodes: NodeModel[];
  readonly connections: ConnectionModel[];
}

export function createCanvasPerformanceFixture(
  rows: number,
  columns: number,
): CanvasPerformanceFixture {
  const nodes: NodeModel[] = [];
  const connections: ConnectionModel[] = [];
  const horizontalGap = 420;
  const verticalGap = 280;
  let nodeId = 1;

  for (let row = 0; row < rows; row += 1) {
    for (let column = 0; column < columns; column += 1) {
      nodes.push(new NodeModel(
        nodeId,
        nodeId,
        0,
        0,
        nodeId * 10,
        '0.0.1',
        `Synthetic ${row + 1}-${column + 1}`,
        column * horizontalGap,
        row * verticalGap,
        320,
        220,
        'Input',
        ['Success'],
        false,
        null,
        null,
        {
          town: 'String',
          score: 'Integer',
          region: 'String',
        },
        {
          Success: {
            town: 'String',
            score: 'Integer',
            region: 'String',
          },
        },
      ));
      nodeId += 1;
    }
  }

  const nodeAt = (row: number, column: number) => nodes[row * columns + column]!;

  for (let row = 0; row < rows; row += 1) {
    for (let column = 0; column < columns; column += 1) {
      const source = nodeAt(row, column);
      const sourceAnchor = source.outputAnchors()[0]!;

      if (column + 1 < columns) {
        const target = nodeAt(row, column + 1);
        connections.push(new ConnectionModel(source, target, sourceAnchor.index, 'left', 62, undefined, [
          { sourceProperty: 'town', targetProperty: 'town' },
          { sourceProperty: 'score', targetProperty: 'score' },
        ]));
      }

      if (row + 1 < rows) {
        const target = nodeAt(row + 1, column);
        connections.push(new ConnectionModel(source, target, sourceAnchor.index, 'left', 62, undefined, [
          { sourceProperty: 'region', targetProperty: 'region' },
        ]));
      }

      if (row + 1 < rows && column + 1 < columns) {
        const target = nodeAt(row + 1, column + 1);
        connections.push(new ConnectionModel(source, target, sourceAnchor.index, 'left', 62, undefined, [
          { sourceProperty: 'score', targetProperty: 'score' },
        ]));
      }
    }
  }

  return {
    nodes,
    connections,
  };
}
