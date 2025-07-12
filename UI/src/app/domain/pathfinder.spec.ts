import { describe, it, expect } from 'vitest';
import { NodeModel } from './node-model';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';

const opts: PathfinderOptions = { gridSize: 10, nodeMargin: 5 };

function rectContainsSegment(node: NodeModel, x1: number, y1: number, x2: number, y2: number): boolean {
  const minX = Math.min(x1, x2);
  const maxX = Math.max(x1, x2);
  const minY = Math.min(y1, y2);
  const maxY = Math.max(y1, y2);
  return !(maxX < node.x || minX > node.x + node.width || maxY < node.y || minY > node.y + node.height);
}

describe('pathfinder', () => {
  it('creates a straight path with no obstacles', () => {
    const segments = orthogonalSegments(0, 0, 40, 0, opts);
    expect(segments[0]).toEqual({ x1: 0, y1: 0, x2: 10, y2: 0 });
    const last = segments[segments.length - 1];
    expect(last).toEqual({ x1: 30, y1: 0, x2: 40, y2: 0 });
  });

  it('routes around an obstacle', () => {
    const obstacle = new NodeModel(1, 'obs', 20, -10, 20, 20);
    const segments = orthogonalSegments(0, 0, 60, 0, opts, [obstacle]);
    for (const s of segments) {
      expect(rectContainsSegment(obstacle, s.x1, s.y1, s.x2, s.y2)).toBe(false);
    }
    const last = segments[segments.length - 1];
    expect(last.x2).toBe(60);
    expect(last.y2).toBe(0);
  });
  it('draws initial segment to buffer edge when leaving source', () => {
    const src = new NodeModel(1, 'src', 0, 0, 40, 40);
    const anchor = src.anchorPositions()[3]; // right side
    const ex = anchor.x + 30;
    const ey = anchor.y;
    const segs = orthogonalSegments(
      anchor.x,
      anchor.y,
      ex,
      ey,
      opts,
      [],
      undefined,
      undefined,
      'right',
      src,
    );
    expect(segs[0]).toEqual({ x1: anchor.x, y1: anchor.y, x2: anchor.x + opts.nodeMargin, y2: anchor.y });
  });

  it('returns direct segment while inside source buffer', () => {
    const src = new NodeModel(1, 'src', 0, 0, 40, 40);
    const anchor = src.anchorPositions()[3];
    const ex = anchor.x + 2; // within margin
    const ey = anchor.y;
    const segs = orthogonalSegments(
      anchor.x,
      anchor.y,
      ex,
      ey,
      opts,
      [],
      undefined,
      undefined,
      'right',
      src,
    );
    expect(segs.length).toBe(1);
    expect(segs[0]).toEqual({ x1: anchor.x, y1: anchor.y, x2: ex, y2: ey });
  });

  it('stops path at destination buffer before snapping to edge', () => {
    const src = new NodeModel(1, 'src', 0, 0, 40, 40);
    const dst = new NodeModel(2, 'dst', 83, 57, 40, 40);
    const anchor = src.anchorPositions()[3];
    const ex = dst.x; // left edge
    const ey = dst.y + dst.height / 2;
    const segs = orthogonalSegments(
      anchor.x,
      anchor.y,
      ex,
      ey,
      opts,
      [],
      'left',
      dst,
      'right',
      src,
    );
    const last = segs[segs.length - 1];
    expect(last.x2).toBe(ex);
    expect(last.y2).toBe(ey);
    const prev = segs[segs.length - 2];
    const targetX = 80; // snap(dst.x - margin)
    expect(prev.x2).toBe(targetX);
  });

  it('snaps endpoint inside destination buffer to the border', () => {
    const src = new NodeModel(1, 'src', 0, 0, 40, 40);
    const dst = new NodeModel(2, 'dst', 90, 60, 40, 40);
    const anchor = src.anchorPositions()[3];
    const ex = dst.x - 2; // inside left buffer
    const ey = dst.y + dst.height / 2;
    const segs = orthogonalSegments(
      anchor.x,
      anchor.y,
      ex,
      ey,
      opts,
      [],
      'left',
      dst,
      'right',
      src,
    );
    for (let i = 1; i < segs.length; i++) {
      expect(segs[i - 1].x2).toBe(segs[i].x1);
      expect(segs[i - 1].y2).toBe(segs[i].y1);
    }
    const last = segs[segs.length - 1];
    expect(last.x2).toBe(dst.x);
    expect(last.y2).toBe(ey);
  });
});
