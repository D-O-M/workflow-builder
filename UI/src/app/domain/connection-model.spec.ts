import { describe, it, expect } from 'vitest';
import { ConnectionModel } from './connection-model';
import { NodeModel, Side } from './node-model';
import { PathfinderOptions } from './pathfinder';

const opts: PathfinderOptions = { gridSize: 10, nodeMargin: 5 };

function createNode(
  id: number,
  name: string,
  x: number,
  y: number,
  options?: {
    inputChannelName?: string | null;
    outputChannelNames?: string[];
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
    320,
    220,
    options?.inputChannelName ?? null,
    options?.outputChannelNames ?? [],
    false,
  );
}

describe('ConnectionModel segments', () => {
  const source = createNode(1, 'Lookup', 0, 0, { outputChannelNames: ['Success'] });
  const destination = createNode(2, 'Forecast', 420, 80, {
    inputChannelName: 'Input',
    outputChannelNames: ['Done'],
  });
  const anchor = source.outputAnchors()[0];

  for (const side of ['left', 'right', 'top', 'bottom'] as Side[]) {
    it(`forms an unbroken line to destination side ${side}`, () => {
      const connection = new ConnectionModel(
        source,
        destination,
        anchor.index,
        side,
        side === 'left' || side === 'right' ? destination.height / 2 : destination.width / 2,
      );
      const segments = connection.segments(opts);

      expect(segments[0].x1).toBe(anchor.x);
      expect(segments[0].y1).toBe(anchor.y);
      for (let i = 1; i < segments.length; i++) {
        expect(segments[i - 1].x2).toBe(segments[i].x1);
        expect(segments[i - 1].y2).toBe(segments[i].y1);
      }
    });
  }
});
