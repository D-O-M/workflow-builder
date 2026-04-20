import { describe, it, expect } from 'vitest';
import { NodeModel } from './node-model';
import { PathfinderOptions, orthogonalSegments } from './pathfinder';

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

describe('pathfinder', () => {
  it('creates a straight path with no obstacles', () => {
    const segments = orthogonalSegments(0, 0, 40, 0, opts);
    expect(segments[0]).toEqual({ x1: 0, y1: 0, x2: 10, y2: 0 });
    const last = segments[segments.length - 1];
    expect(last).toEqual({ x1: 30, y1: 0, x2: 40, y2: 0 });
  });

  it('returns a valid path when obstacles are provided', () => {
    const obstacle = createNode(1, 'Obstacle', 20, -10);
    const segments = orthogonalSegments(0, 0, 60, 0, opts, [obstacle]);
    expect(segments.length).toBeGreaterThan(0);
    const last = segments[segments.length - 1];
    expect(last.x2).toBe(60);
    expect(last.y2).toBe(0);
  });

  it('draws initial segment to buffer edge when leaving source', () => {
    const src = createNode(1, 'Source', 0, 0, { outputChannelNames: ['Success'] });
    const anchor = src.outputAnchors()[0];
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
    const src = createNode(1, 'Source', 0, 0, { outputChannelNames: ['Success'] });
    const anchor = src.outputAnchors()[0];
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
    const src = createNode(1, 'Source', 0, 0, { outputChannelNames: ['Success'] });
    const dst = createNode(2, 'Destination', 403, 57, { inputChannelName: 'Input' });
    const anchor = src.outputAnchors()[0];
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
    const targetX = 400; // snap(dst.x - margin)
    expect(prev.x2).toBe(targetX);
  });

  it('snaps endpoint inside destination buffer to the border', () => {
    const src = createNode(1, 'Source', 0, 0, { outputChannelNames: ['Success'] });
    const dst = createNode(2, 'Destination', 410, 60, { inputChannelName: 'Input' });
    const anchor = src.outputAnchors()[0];
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
