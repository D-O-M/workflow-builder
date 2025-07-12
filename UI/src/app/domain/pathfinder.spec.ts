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
});
