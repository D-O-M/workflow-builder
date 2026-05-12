import { describe, expect, it } from 'vitest';
import { SpatialIndex } from './canvas-render-spatial-index';

describe('SpatialIndex', () => {
  it('queries candidates by bounds without duplicating multi-bucket items', () => {
    const index = new SpatialIndex<string>(100);
    index.insert({ left: 40, top: 40, right: 160, bottom: 160 }, 'large');
    index.insert({ left: 260, top: 260, right: 280, bottom: 280 }, 'far');

    expect(index.queryBounds({ left: 0, top: 0, right: 120, bottom: 120 })).toEqual(['large']);
  });
});
