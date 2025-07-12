import { describe, it, expect } from 'vitest';
import { ConnectionModel } from './connection-model';
import { NodeModel, Side } from './node-model';
import { PathfinderOptions } from './pathfinder';

const opts: PathfinderOptions = { gridSize: 10, nodeMargin: 5 };

const source = new NodeModel(1, 'src', 0, 0, 40, 40);
const dest = new NodeModel(2, 'dest', 100, 60, 40, 40);

const anchorIndex: Record<Side, number> = {
  top: 0,
  right: 3,
  bottom: 6,
  left: 9,
};

describe('ConnectionModel segments', () => {
  const sides: Side[] = ['left', 'right', 'top', 'bottom'];
  for (const start of sides) {
    for (const end of sides) {
      it(`forms unbroken line from ${start} to ${end}`, () => {
        const conn = new ConnectionModel(
          source,
          dest,
          anchorIndex[start],
          end,
          end === 'left' || end === 'right' ? dest.height / 2 : dest.width / 2,
        );
        const segs = conn.segments(opts);
        const anchor = source.anchorPositions()[anchorIndex[start]];
        expect(segs[0].x1).toBe(anchor.x);
        expect(segs[0].y1).toBe(anchor.y);
        for (let i = 1; i < segs.length; i++) {
          expect(segs[i - 1].x2).toBe(segs[i].x1);
          expect(segs[i - 1].y2).toBe(segs[i].y1);
        }
      });
    }
  }
});
